using System.Collections.Concurrent;
using System.Reflection;

using Altruist.Contracts;
using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Shared startup helpers for the Postgres <c>[ServiceConfiguration]</c> classes (see
/// <see cref="PostgresDatabaseConfiguration"/>): assembly scanning, keyspace registration, <c>[Transactional]</c>
/// proxy wrapping and the <see cref="NpgsqlDataSource"/> registration. Not meant to be used by application code.
/// </summary>
public abstract class PostgresConfigurationBase
{
    // ----------------- discovery helpers -----------------

    /// <summary>All non-dynamic assemblies currently loaded in the app domain.</summary>
    protected static Assembly[] DiscoverAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

    /// <summary>Concrete <see cref="IKeyspace"/> classes annotated with <see cref="KeyspaceAttribute"/>.</summary>
    /// <param name="assemblies">Assemblies to scan.</param>
    protected static IEnumerable<Type> FindSchemaTypes(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesWithAttribute<KeyspaceAttribute>(assemblies)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IKeyspace).IsAssignableFrom(t));

    /// <summary>Types implementing <see cref="IDatabaseInitializer"/> (run once after migration at startup).</summary>
    /// <param name="assemblies">Assemblies to scan.</param>
    protected static IEnumerable<Type> FindInitializers(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesImplementing<IDatabaseInitializer>(assemblies);

    /// <summary>
    /// Schema of a model: the trimmed <c>Keyspace</c> of its <see cref="VaultAttribute"/> (or subclass, inherited), or
    /// <c>public</c> when that is empty or the attribute is missing. Note the attribute's own default keyspace is
    /// <c>altruist</c>.
    /// </summary>
    /// <param name="modelType">The vault model type.</param>
    protected static string GetSchemaName(Type modelType)
    {
        // Works for [Vault], [Prefab], and any future : VaultAttribute attribute.
        var va = modelType.GetCustomAttribute<VaultAttribute>(inherit: true);
        if (!string.IsNullOrWhiteSpace(va?.Keyspace))
            return va!.Keyspace!.Trim();

        return "public";
    }

    // ----------------- schema registration -----------------

    /// <summary>
    /// Registers each keyspace class as a singleton (built with configuration binding) and also as an
    /// <see cref="IKeyspace"/>. Types already registered are skipped.
    /// </summary>
    /// <param name="services">Service collection being configured.</param>
    /// <param name="cfg">Application configuration used to construct the keyspaces.</param>
    /// <param name="schemaTypes">Keyspace types to register.</param>
    /// <param name="logger">Logger passed to the resolver.</param>
    protected static void RegisterSchemas(
        IServiceCollection services,
        IConfiguration cfg,
        Type[] schemaTypes,
        ILogger logger)
    {
        foreach (var schemaType in schemaTypes)
        {
            if (services.Any(d => d.ServiceType == schemaType))
                continue;

            services.AddSingleton(schemaType, sp =>
            {
                var inst = DependencyResolver.CreateWithConfiguration(sp, cfg, schemaType, logger);
                return inst!;
            });

            services.AddSingleton(typeof(IKeyspace), sp => (IKeyspace)sp.GetRequiredService(schemaType));
        }
    }

    // ----------------- transactional wrapping -----------------

    /// <summary>
    /// Makes every service resolved through an interface whose implementation has <see cref="TransactionalAttribute"/>
    /// methods come back wrapped in a <see cref="TransactionalDecorator{T}"/> proxy, keeping the original lifetime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every non-keyed registration whose service type is an interface implemented by a <c>[Transactional]</c> class is
    /// replaced by a factory that builds the original service (type, instance or factory registration, including the
    /// forwarding registrations <c>[Service(typeof(IMyService))]</c> creates) and, when the instance's class has
    /// <c>[Transactional]</c> methods, returns the proxy around it. The decision is made on the actual instance, so it
    /// works whichever way the service was registered.
    /// </para>
    /// <para>
    /// A <see cref="DispatchProxy"/> can only implement an interface: resolving the class itself (or calling methods on
    /// <c>this</c>) bypasses the transaction. Keyed registrations are not wrapped. Already wrapped registrations are
    /// skipped, so running this again only wraps services registered since the previous run: it runs early (so
    /// database initializers see proxies) and again after every other configuration step (see
    /// <see cref="PostgresTransactionalConfiguration"/>).
    /// </para>
    /// </remarks>
    /// <param name="services">Service collection being configured.</param>
    /// <param name="assemblies">Assemblies scanned for <c>[Transactional]</c> methods.</param>
    protected static void RegisterTransactionalServices(IServiceCollection services, Assembly[] assemblies)
    {
        TransactionalRegistry.WarmUp(assemblies);

        var interfaces = TransactionalRegistry.GetAll()
            .Select(m => m.ServiceType ?? m.DeclaringType)
            .SelectMany(t => t.GetInterfaces())
            .ToHashSet();

        for (var i = 0; i < services.Count; i++)
        {
            var d = services[i];
            if (d.IsKeyedService || !interfaces.Contains(d.ServiceType) || d.ImplementationFactory?.Target is TransactionalFactory)
                continue;

            var original = d.ImplementationFactory
                ?? (d.ImplementationInstance is { } instance
                    ? _ => instance
                    : sp => ActivatorUtilities.CreateInstance(sp, d.ImplementationType!));
            services[i] = new ServiceDescriptor(d.ServiceType, new TransactionalFactory(d.ServiceType, original).Create, d.Lifetime);
        }
    }

    /// <summary>Builds the original service and wraps it in a transactional proxy when its class has <c>[Transactional]</c> methods.</summary>
    private sealed class TransactionalFactory
    {
        private static readonly ConcurrentDictionary<Type, bool> IsTransactional = new();

        private readonly Type _serviceType;
        private readonly Func<IServiceProvider, object> _original;
        private readonly MethodInfo _createProxy;

        public TransactionalFactory(Type serviceType, Func<IServiceProvider, object> original)
        {
            _serviceType = serviceType;
            _original = original;
            _createProxy = typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == nameof(DispatchProxy.Create) && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(serviceType, typeof(TransactionalDecorator<>).MakeGenericType(serviceType));
        }

        public object Create(IServiceProvider sp)
        {
            var inner = _original(sp);
            if (inner is null || !IsTransactional.GetOrAdd(inner.GetType(), RegisterTransactionalMethods))
                return inner!;

            var proxy = _createProxy.Invoke(null, null)!;
            var decorator = (dynamic)proxy;
            decorator.Inner = (dynamic)inner;
            decorator.DataSource = sp.GetRequiredService<NpgsqlDataSource>();
            return proxy;
        }

        /// <summary>Registers the type's <c>[Transactional]</c> methods (idempotent); true when it has any.</summary>
        private static bool RegisterTransactionalMethods(Type type)
        {
            var found = false;
            foreach (var method in type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.GetCustomAttribute<TransactionalAttribute>(inherit: true) is not { } attr)
                    continue;
                TransactionalRegistry.Register(method, attr, type);
                found = true;
            }
            return found;
        }
    }

    // ----------------- Npgsql data source -----------------

    /// <summary>
    /// Registers a singleton <see cref="NpgsqlDataSource"/> (used by <see cref="TransactionalDecorator{T}"/>, the E2E
    /// reset endpoint and test schema isolation), once.
    /// </summary>
    /// <remarks>
    /// Built from the default provider's connection string (<see cref="PgSqlDbProvider.GetConnectionString"/>), so it
    /// targets the same database with the same settings as the vaults: <c>altruist:persistence:database</c>
    /// <c>host</c>, <c>port</c>, <c>username</c>, <c>password</c>, <c>database</c>, <c>pooling</c>,
    /// <c>max-pool-size</c>, <c>ssl-mode</c> and the UTC session time zone. A transaction opened on it and the vault
    /// calls joining that transaction therefore always run against one database.
    /// </remarks>
    /// <param name="services">Service collection being configured.</param>
    protected static void RegisterNpgsqlDataSource(IServiceCollection services)
    {
        if (services.Any(d => d.ServiceType == typeof(NpgsqlDataSource)))
            return;

        services.AddSingleton(sp => NpgsqlDataSource.Create(sp.GetRequiredService<PgSqlDbProvider>().GetConnectionString()));
    }
}

/// <summary>
/// Late startup step (runs after every other <c>[ServiceConfiguration]</c> except the final startup one) that wraps
/// <c>[Transactional]</c> services registered after <see cref="PostgresDatabaseConfiguration"/> ran. Not meant to be
/// used by application code.
/// </summary>
[ServiceConfiguration(order: int.MaxValue - 1)]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresTransactionalConfiguration : PostgresConfigurationBase, IAltruistConfiguration
{
    /// <inheritdoc/>
    public bool IsConfigured { get; set; }

    /// <summary>Wraps the remaining <c>[Transactional]</c> services; see <see cref="PostgresConfigurationBase.RegisterTransactionalServices"/>.</summary>
    /// <param name="services">Service collection being configured.</param>
    /// <returns>A completed task.</returns>
    public Task Configure(IServiceCollection services)
    {
        RegisterTransactionalServices(services, DiscoverAssemblies());
        IsConfigured = true;
        return Task.CompletedTask;
    }
}
