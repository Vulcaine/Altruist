using System.Collections;
using System.Reflection;

using Altruist.Migrations;
using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// Common helper logic shared by vault + prefab Postgres configurations.
/// Does NOT implement IDatabaseConfiguration itself.
/// </summary>
public abstract class PostgresConfigurationBase
{
    // ----------------- discovery helpers -----------------

    protected static Assembly[] DiscoverAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

    protected static IEnumerable<Type> FindSchemaTypes(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesWithAttribute<KeyspaceAttribute>(assemblies)
            .Where(t =>
                t is not null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(IKeyspace).IsAssignableFrom(t))!;

    protected static string GetSchemaName(Type modelType)
    {
        // Reuse VaultAttribute.Keyspace as Postgres schema name
        var va = modelType.GetCustomAttribute<VaultAttribute>();
        return string.IsNullOrWhiteSpace(va?.Keyspace) ? "public" : va!.Keyspace!;
    }

    // ----------------- schemas -----------------

    /// <summary>
    /// Idempotent schema registration – if a given schemaType was already registered
    /// as a ServiceType, we skip adding it again.
    /// </summary>
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

    protected static IKeyspace? ResolveSchemaInstance(
        IServiceProvider sp,
        Type[] schemaTypes,
        List<IKeyspace> allSchemaInstances,
        string schemaName)
    {
        var schemaInstance = allSchemaInstances.FirstOrDefault(s =>
            string.Equals(s.Name, schemaName, StringComparison.OrdinalIgnoreCase));

        if (schemaInstance is null)
        {
            var schemaType = ResolveSchemaTypeByName(schemaTypes, schemaName);
            if (schemaType is not null)
                schemaInstance = (IKeyspace)sp.GetRequiredService(schemaType);
        }

        return schemaInstance;
    }

    protected static Type? ResolveSchemaTypeByName(Type[] schemaTypes, string schemaName) =>
        schemaTypes.FirstOrDefault(t =>
        {
            var attr = t.GetCustomAttribute<KeyspaceAttribute>();
            return attr != null && string.Equals(attr.Name, schemaName, StringComparison.OrdinalIgnoreCase);
        });

    /// <summary>
    /// Shared bootstrap logic for a set of IVaultModel types (vaults or prefabs).
    /// </summary>
    protected static async Task BootstrapModelsAsync(
        IServiceCollection services,
        Type[] schemaTypes,
        Type[] modelTypes,
        string logPrefix)
    {
        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger(logPrefix);

        var provider = sp.GetService<ISqlDatabaseProvider>();
        var migrator = sp.GetService<IVaultSchemaMigrator>();
        var keyspaces = sp.GetServices<IKeyspace>().ToList();

        if (provider is null)
        {
            logger.LogWarning("⚠️ No ISqlDatabaseProvider registered; skipping bootstrap for {Prefix}.", logPrefix);
            return;
        }

        if (migrator is null)
        {
            logger.LogWarning("⚠️ No IVaultSchemaMigrator registered; skipping schema migration for {Prefix}.", logPrefix);
            return;
        }

        if (modelTypes.Length == 0)
        {
            logger.LogInformation("ℹ️ No model types found for {Prefix}.", logPrefix);
            return;
        }

        var groups = modelTypes.GroupBy(GetSchemaName);

        foreach (var group in groups)
        {
            var schemaName = group.Key;

            var schemaInstance = ResolveSchemaInstance(sp, schemaTypes, keyspaces, schemaName)
                                 ?? new DefaultSchema(schemaName);

            await provider.ConnectAsync();
            await provider.CreateSchemaAsync(schemaInstance.Name, null);

            var typesInSchema = group.ToArray();
            await migrator.Migrate(schemaInstance, typesInSchema);

            await CreateTablesAndRunHooksAsync(sp, logger, typesInSchema);
        }

        logger.LogInformation(
            "🐘 PostgreSQL {Prefix} bootstrap complete. {Count} model(s) processed.",
            logPrefix,
            modelTypes.Length);
    }

    /// <summary>
    /// Shared BEFORE / IOnVaultCreate / AFTER hook runner used for any IVaultModel types.
    /// </summary>
    protected static async Task CreateTablesAndRunHooksAsync(
        IServiceProvider sp,
        ILogger logger,
        Type[] modelTypes)
    {
        foreach (var modelType in modelTypes)
        {
            IVaultModel? instance = null;

            try
            {
                instance = modelType.GetConstructor(Type.EmptyTypes)!.Invoke(null) as IVaultModel;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to construct instance for {ModelType}. Reason: {Message}",
                    modelType.Name, ex.Message);
                continue;
            }

            if (instance is null)
            {
                logger.LogError("Instance for {ModelType} is null or does not implement IVaultModel.",
                    modelType.Name);
                continue;
            }

            // BEFORE hook
            try
            {
                if (instance is IBeforeVaultCreate before)
                    await before.BeforeCreateAsync(sp);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to run BEFORE actions for {ModelType}. Reason: {Message}",
                    modelType.Name, ex.Message);
            }

            // PRELOAD hook (IOnVaultCreate<T>)
            try
            {
                var preloadInterface = instance
                    .GetType()
                    .GetInterfaces()
                    .FirstOrDefault(i =>
                        i.IsGenericType &&
                        i.GetGenericTypeDefinition() == typeof(IOnVaultCreate<>));

                if (preloadInterface is not null)
                {
                    var onCreateAsync = preloadInterface.GetMethod("OnCreateAsync")!;
                    var taskObj = (Task)onCreateAsync.Invoke(instance, new object[] { sp })!;
                    await taskObj.ConfigureAwait(false);

                    var resultProp = taskObj.GetType().GetProperty("Result")!;
                    var resultObj = resultProp.GetValue(taskObj);

                    if (resultObj is IEnumerable enumerable)
                    {
                        int loadedCount = 0;
                        foreach (var _ in enumerable)
                            loadedCount++;

                        if (loadedCount > 0)
                        {
                            var vaultIface = typeof(IVault<>).MakeGenericType(modelType);
                            var vault = sp.GetRequiredService(vaultIface);

                            var countMethod = vaultIface.GetMethod("CountAsync")!;
                            var countTask = (Task)countMethod.Invoke(vault, Array.Empty<object>())!;
                            await countTask.ConfigureAwait(false);
                            var count = (long)countTask.GetType().GetProperty("Result")!.GetValue(countTask)!;

                            if (count == 0)
                            {
                                var castMethod = typeof(Enumerable).GetMethod("Cast", BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(modelType);
                                var toListMethod = typeof(Enumerable).GetMethod("ToList", BindingFlags.Public | BindingFlags.Static)!.MakeGenericMethod(modelType);
                                var typedList = toListMethod.Invoke(
                                    null,
                                    new object[] { castMethod.Invoke(null, new object[] { resultObj })! })!;

                                var saveBatch = vaultIface.GetMethod("SaveBatchAsync", new[]
                                    {
                                        typeof(IEnumerable<>).MakeGenericType(modelType),
                                        typeof(bool?)
                                    })
                                               ?? vaultIface.GetMethod("SaveBatchAsync", new[]
                                                   { typeof(IEnumerable<>).MakeGenericType(modelType) });

                                object? saveTaskObj;
                                if (saveBatch!.GetParameters().Length == 2)
                                    saveTaskObj = saveBatch.Invoke(vault, new object?[] { typedList, null });
                                else
                                    saveTaskObj = saveBatch.Invoke(vault, new object?[] { typedList });

                                await ((Task)saveTaskObj!).ConfigureAwait(false);
                                logger.LogInformation("Streamed {Count} items into {ModelType}.",
                                    loadedCount, modelType.Name);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to run PRELOAD actions for {ModelType}. Reason: {Message}",
                    modelType.Name, ex.Message);
            }

            try
            {
                if (instance is IAfterVaultCreate after)
                    await after.AfterCreateAsync(sp);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to run AFTER actions for {ModelType}. Reason: {Message}",
                    modelType.Name, ex.Message);
            }
        }
    }
}
