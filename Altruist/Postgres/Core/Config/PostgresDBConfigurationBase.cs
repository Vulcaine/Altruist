// PostgresConfigurationBase.cs
using System.Reflection;

using Altruist.Migrations;
using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Persistence.Postgres;

public abstract class PostgresConfigurationBase
{
    // ----------------- discovery helpers -----------------

    protected static Assembly[] DiscoverAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

    protected static IEnumerable<Type> FindSchemaTypes(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesWithAttribute<KeyspaceAttribute>(assemblies)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IKeyspace).IsAssignableFrom(t));

    protected static IEnumerable<Type> FindModelTypes(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesWithAttribute<VaultAttribute>(assemblies)
            .Where(t => t.IsClass && !t.IsAbstract && typeof(IVaultModel).IsAssignableFrom(t));

    protected static IEnumerable<Type> FindInitializers(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesImplementing<IDatabaseInitializer>(assemblies);

    protected static string GetSchemaName(Type modelType)
    {
        // Works for [Vault], [Prefab], and any future : VaultAttribute attribute.
        var va = modelType.GetCustomAttribute<VaultAttribute>(inherit: false);
        if (!string.IsNullOrWhiteSpace(va?.Keyspace))
            return va!.Keyspace!.Trim();

        return "public";
    }

    // ----------------- schema registration (global guarded) -----------------

    private static int _schemasRegistered;

    protected static void EnsureSchemasRegistered(
        IServiceCollection services,
        IConfiguration cfg,
        Type[] schemaTypes,
        string loggerName)
    {
        if (schemaTypes.Length == 0)
            return;

        // global guard: register schemas only once even if multiple configurations run
        if (Interlocked.CompareExchange(ref _schemasRegistered, 1, 0) != 0)
            return;

        // best-effort logger factory (avoid failing if logging not registered yet)
        using var tmp = services.BuildServiceProvider();
        var loggerFactory = tmp.GetService<ILoggerFactory>() ?? NullLoggerFactory.Instance;
        var logger = loggerFactory.CreateLogger(loggerName);

        RegisterSchemas(services, cfg, schemaTypes, logger);
    }

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

    // ----------------- transactional wrapping (unchanged, but moved to base so it's reusable) -----------------

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
            decorator.DataSource = sp.GetRequiredService<Npgsql.NpgsqlDataSource>();

            return proxy;
        };
    }

    // ----------------- global bootstrap coordinator -----------------

    /// <summary>
    /// Coordinates schema migration + initializer execution so it runs once
    /// after BOTH the vault config and prefab config have had a chance to register services.
    /// </summary>
    protected internal static class DatabaseBootstrapCoordinator
    {
        private static readonly object Gate = new();

        private static readonly HashSet<Type> AllModels = new();
        private static readonly HashSet<Type> AllInitializers = new();

        private static int _vaultConfigured;
        private static int _prefabConfigured;
        private static int _bootstrapped;

        public static void AddModels(IEnumerable<Type> modelTypes)
        {
            lock (Gate)
            {
                foreach (var t in modelTypes)
                    AllModels.Add(t);
            }
        }

        public static void AddInitializers(IEnumerable<Type> initializerTypes)
        {
            lock (Gate)
            {
                foreach (var t in initializerTypes)
                    AllInitializers.Add(t);
            }
        }

        public static void MarkVaultConfigured() => Interlocked.Exchange(ref _vaultConfigured, 1);
        public static void MarkPrefabConfigured() => Interlocked.Exchange(ref _prefabConfigured, 1);

        public static async Task TryBootstrapOnceAsync(IServiceCollection services, string logPrefix)
        {
            // Wait until both have run
            if (Volatile.Read(ref _vaultConfigured) == 0 || Volatile.Read(ref _prefabConfigured) == 0)
                return;

            // Only one winner runs bootstrap
            if (Interlocked.CompareExchange(ref _bootstrapped, 1, 0) != 0)
                return;

            Type[] modelTypes;
            Type[] initializerTypes;

            lock (Gate)
            {
                modelTypes = AllModels.ToArray();
                initializerTypes = AllInitializers.ToArray();
            }

            using var sp = services.BuildServiceProvider();
            var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(logPrefix);

            var provider = sp.GetService<ISqlDatabaseProvider>();
            var migrator = sp.GetService<IVaultSchemaMigrator>();

            if (provider is null)
            {
                logger.LogWarning("⚠️ No ISqlDatabaseProvider registered; skipping bootstrap.");
                return;
            }

            if (migrator is null)
            {
                logger.LogWarning("⚠️ No IVaultSchemaMigrator registered; skipping schema migration.");
                return;
            }

            if (modelTypes.Length == 0)
            {
                logger.LogInformation("ℹ️ No model types found; skipping schema migration.");
                await RunInitializersAsync(sp, initializerTypes, logger).ConfigureAwait(false);
                return;
            }

            // Determine schemas/keyspaces from the models
            var schemaNames = modelTypes
                .Select(GetSchemaName)
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            await provider.ConnectAsync().ConfigureAwait(false);

            foreach (var schemaName in schemaNames)
                await provider.CreateSchemaAsync(schemaName, null).ConfigureAwait(false);

            await migrator.Migrate(modelTypes).ConfigureAwait(false);

            await RunInitializersAsync(sp, initializerTypes, logger).ConfigureAwait(false);

            logger.LogInformation(
                "🐘 PostgreSQL bootstrap complete. {Count} model(s), {Init} initializer(s).",
                modelTypes.Length,
                initializerTypes.Length);
        }

        private static async Task RunInitializersAsync(
            IServiceProvider sp,
            Type[] initializerTypes,
            ILogger logger)
        {
            if (initializerTypes.Length == 0)
                return;

            var ordered = initializerTypes
                .Select(t => ActivatorUtilities.CreateInstance(sp, t))
                .Cast<IDatabaseInitializer>()
                .OrderBy(i => i.Order)
                .ThenBy(i => i.GetType().FullName, StringComparer.Ordinal)
                .ToList();

            foreach (var init in ordered)
            {
                try
                {
                    await init.InitializeAsync(sp).ConfigureAwait(false);
                    logger.LogInformation("✔ Initializer {Init} executed.", init.GetType().Name);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "❌ Initializer {Init} failed: {Message}", init.GetType().Name, ex.Message);
                }
            }
        }
    }
}
