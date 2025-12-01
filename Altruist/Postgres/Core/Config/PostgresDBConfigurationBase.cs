using System.Reflection;

using Altruist.Migrations;
using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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
        var va = modelType.GetCustomAttribute<VaultAttribute>();
        return string.IsNullOrWhiteSpace(va?.Keyspace) ? "public" : va!.Keyspace!;
    }

    // ----------------- schema registration -----------------

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

    // ----------------- bootstrap logic -----------------

    protected static async Task BootstrapModelsAsync(
        IServiceCollection services,
        Type[] modelTypes,
        Type[] initializerTypes,
        string logPrefix)
    {
        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(logPrefix);

        var provider = sp.GetService<ISqlDatabaseProvider>();
        var migrator = sp.GetService<IVaultSchemaMigrator>();
        var keyspaces = sp.GetServices<IKeyspace>().ToList();

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
            logger.LogInformation("ℹ️ No model types found.");
            return;
        }

        var groups = modelTypes.GroupBy(GetSchemaName);

        foreach (var group in groups)
        {
            var schemaName = group.Key;
            var schemaInstance = keyspaces.FirstOrDefault(s => s.Name == schemaName)
                                 ?? new DefaultSchema(schemaName);

            await provider.ConnectAsync();
            await provider.CreateSchemaAsync(schemaInstance.Name, null);

            var typesInSchema = group.ToArray();
            await migrator.Migrate(schemaInstance, typesInSchema);
        }

        // Run initializers: EXACT ORDER
        await RunInitializersAsync(sp, initializerTypes, logger);

        logger.LogInformation(
            "🐘 PostgreSQL {Prefix} bootstrap complete. {Count} vault model(s), {Init} initializer(s).",
            logPrefix,
            modelTypes.Length,
            initializerTypes.Length);
    }

    // ----------------- initializer system -----------------

    private static async Task RunInitializersAsync(
        IServiceProvider sp,
        Type[] initializerTypes,
        ILogger logger)
    {
        if (initializerTypes.Length == 0)
            return;

        // NO DEPENDENCY ORDERING — order = AS DISCOVERED
        var allResults = new List<IVaultModel>();

        foreach (var initType in initializerTypes)
        {
            try
            {
                var initializer = (IDatabaseInitializer)ActivatorUtilities.CreateInstance(sp, initType);
                var result = await initializer.InitializeAsync(sp);

                if (result != null && result.Any())
                {
                    allResults.AddRange(result);
                    logger.LogInformation("✔ Initializer {Init} executed. Inserted {Count} items.",
                        initType.Name, result.Count());
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "❌ Initializer {Init} failed: {Message}",
                    initType.Name, ex.Message);
            }
        }

        if (allResults.Count == 0)
            return;

        // Group by vault type and save
        var groups = allResults.GroupBy(m => m.GetType());

        foreach (var group in groups)
        {
            var modelType = group.Key;
            var list = group.ToList();

            try
            {
                var vaultType = typeof(IVault<>).MakeGenericType(modelType);
                var vault = sp.GetRequiredService(vaultType);

                var saveBatch =
                    vaultType.GetMethod("SaveBatchAsync", new[] { typeof(IEnumerable<>).MakeGenericType(modelType) })
                    ?? vaultType.GetMethod("SaveBatchAsync");

                var castedList = typeof(Enumerable)
                    .GetMethod("Cast")!
                    .MakeGenericMethod(modelType)
                    .Invoke(null, new object[] { list })!;

                var toList = typeof(Enumerable)
                    .GetMethod("ToList")!
                    .MakeGenericMethod(modelType)
                    .Invoke(null, new[] { castedList })!;

                var task = (Task)saveBatch!.Invoke(vault, new[] { toList })!;
                await task.ConfigureAwait(false);

                logger.LogInformation("📦 Inserted {Count} items into vault {Model}.",
                    list.Count, modelType.Name);
            }
            catch (Exception ex)
            {
                logger.LogError(ex,
                    "❌ Failed inserting initializer data for {Model}: {Message}",
                    modelType.Name, ex.Message);
            }
        }
    }
}
