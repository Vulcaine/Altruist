using System.Reflection;

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
    /// Wraps every service whose implementation has <see cref="TransactionalAttribute"/> methods in a
    /// <see cref="TransactionalDecorator{T}"/> proxy, keeping the original lifetime.
    /// </summary>
    /// <remarks>
    /// Rebuilds the whole service collection (clear + re-add), so it only sees services registered before it runs.
    /// Only descriptors with an <c>ImplementationType</c> are wrapped (factory or instance registrations are not).
    /// The proxy is a <see cref="DispatchProxy"/>, so the service type must be an interface: a class registered as
    /// itself cannot be proxied. The inner instance is created with <see cref="ActivatorUtilities"/>.
    /// </remarks>
    /// <param name="services">Service collection being configured.</param>
    /// <param name="assemblies">Assemblies scanned for <c>[Transactional]</c> methods.</param>
    protected static void RegisterTransactionalServices(IServiceCollection services, Assembly[] assemblies)
    {
        TransactionalRegistry.WarmUp(assemblies);

        var descriptors = services.ToList();
        services.Clear();

        foreach (var descriptor in descriptors)
        {
            if (descriptor.ImplementationType is { } implType &&
                TransactionalRegistry.HasTransactionalMethods(implType))
            {
                services.Add(WrapServiceIfTransactional(descriptor, implType));
            }
            else
            {
                services.Add(descriptor);
            }
        }
    }

    private static ServiceDescriptor WrapServiceIfTransactional(ServiceDescriptor descriptor, Type implType)
    {
        var serviceType = descriptor.ServiceType;
        var lifetime = descriptor.Lifetime;

        var decoratorFactory = BuildDecoratorFactory(serviceType, implType);
        return new ServiceDescriptor(serviceType, decoratorFactory, lifetime);
    }

    private static Func<IServiceProvider, object> BuildDecoratorFactory(Type serviceType, Type implType)
    {
        var useType = implType;

        return sp =>
        {
            var inner = ActivatorUtilities.CreateInstance(sp, useType);
            var proxyServiceType = serviceType.IsAssignableFrom(useType) ? serviceType : useType;

            var createMethod = typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(m => m.Name == nameof(DispatchProxy.Create) && m.GetGenericArguments().Length == 2)
                .MakeGenericMethod(proxyServiceType, typeof(TransactionalDecorator<>).MakeGenericType(proxyServiceType));

            var proxy = createMethod.Invoke(null, null)!;

            var decorator = (dynamic)proxy;
            decorator.Inner = inner;
            decorator.DataSource = sp.GetRequiredService<NpgsqlDataSource>();

            return proxy;
        };
    }

    // ----------------- Npgsql data source -----------------

    /// <summary>
    /// Registers a singleton <see cref="NpgsqlDataSource"/> (used by <see cref="TransactionalDecorator{T}"/>), once.
    /// </summary>
    /// <remarks>
    /// Connection string: <c>ConnectionStrings:postgres</c> if present; otherwise built from
    /// <c>altruist:persistence:database</c> <c>host</c> (default <c>localhost</c>), <c>port</c> (5432), <c>username</c>,
    /// <c>password</c> and <c>database</c>. Unlike <see cref="PgSqlDbProvider"/>, this path does not apply
    /// <c>pooling</c>, <c>max-pool-size</c>, <c>ssl-mode</c>, lower-casing or the UTC session time zone.
    /// </remarks>
    /// <param name="services">Service collection being configured.</param>
    /// <param name="cfg">Application configuration.</param>
    /// <exception cref="InvalidOperationException">
    /// No connection string and the provider is not <c>postgres</c>, or username/password/database is missing.
    /// </exception>
    protected static void RegisterNpgsqlDataSource(IServiceCollection services, IConfiguration cfg)
    {
        if (services.Any(d => d.ServiceType == typeof(NpgsqlDataSource)))
            return;

        var connString = cfg.GetConnectionString("postgres");

        if (string.IsNullOrWhiteSpace(connString))
        {
            var dbSection = cfg.GetSection("altruist:persistence:database");

            var provider = dbSection["provider"];
            if (!string.Equals(provider, "postgres", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Postgres configuration used but provider is '{provider ?? "<null>"}'.");
            }

            var host = dbSection["host"] ?? "localhost";
            var portStr = dbSection["port"] ?? "5432";
            var username = dbSection["username"];
            var password = dbSection["password"];
            var database = dbSection["database"];

            if (string.IsNullOrWhiteSpace(username) ||
                string.IsNullOrWhiteSpace(password) ||
                string.IsNullOrWhiteSpace(database))
            {
                throw new InvalidOperationException(
                    "PostgreSQL configuration is incomplete. " +
                    "Expected altruist:persistence:database:username, :password, :database.");
            }

            if (!int.TryParse(portStr, out var port))
                port = 5432;

            var builder = new NpgsqlConnectionStringBuilder
            {
                Host = host,
                Port = port,
                Username = username,
                Password = password,
                Database = database,
            };

            connString = builder.ConnectionString;
        }

        if (string.IsNullOrWhiteSpace(connString))
            throw new InvalidOperationException("PostgreSQL connection string not found in configuration.");

        services.AddSingleton(_ =>
        {
            var builder = new NpgsqlDataSourceBuilder(connString);
            return builder.Build();
        });
    }
}
