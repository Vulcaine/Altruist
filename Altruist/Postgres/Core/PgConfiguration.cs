/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Collections;
using System.Reflection;

using Altruist.Contracts;
using Altruist.Persistence;
using Altruist.UORM;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Postgres;

public sealed class PostgresDBToken : IDatabaseServiceToken
{
    public static PostgresDBToken Instance { get; } = new PostgresDBToken();
    public IDatabaseConfiguration Configuration => new PostgresDBConfiguration();
    public string Description => "💾 Database: PostgreSQL";
}

public sealed class PostgresVaultRepository<TSchema> : VaultRepository<TSchema>
    where TSchema : class, IKeyspace
{
    public PostgresVaultRepository(IServiceProvider provider, ISqlDatabaseProvider databaseProvider, TSchema schema)
        : base(provider, databaseProvider, schema) { }
}

[ServiceConfiguration]
public sealed class PostgresDBConfiguration : IDatabaseConfiguration
{
    public string DatabaseName => "PostgreSQL";

    public async Task Configure(IServiceCollection services)
    {
        var cfg = AppConfigLoader.Load();
        var assemblies = DiscoverAssemblies();

        var schemaTypes = FindSchemaTypes(assemblies).ToArray();          // optional; falls back to DefaultSchema
        var vaultModelTypes = FindVaultModelTypes(assemblies).ToArray();

        RegisterSchemas(services, cfg, schemaTypes);
        RegisterVaultsAndPopulateRegistry(services, schemaTypes, vaultModelTypes);

        await BootstrapAsync(services, schemaTypes, vaultModelTypes);
    }

    private static void RegisterSchemas(IServiceCollection services, IConfiguration cfg, Type[] schemaTypes)
    {
        foreach (var schemaType in schemaTypes)
        {
            services.AddSingleton(schemaType, sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresDBConfiguration>();
                var inst = DependencyResolver.CreateWithConfiguration(sp, cfg, schemaType, logger);
                _ = DependencyResolver.InvokePostConstructAsync(inst, sp, cfg, logger);
                return inst!;
            });

            // Expose as IKeyspace (logical namespace)
            services.AddSingleton(typeof(IKeyspace), sp => (IKeyspace)sp.GetRequiredService(schemaType));
        }
    }

    private static void RegisterVaultsAndPopulateRegistry(
        IServiceCollection services,
        Type[] schemaTypes,
        Type[] vaultModelTypes)
    {
        foreach (var modelType in vaultModelTypes)
        {
            var schemaName = GetSchemaName(modelType); // from [Vault(Keyspace=...)] or defaults to "public"

            VaultRegistry.Register(modelType, schemaName);

            var vaultIface = typeof(IVault<>).MakeGenericType(modelType);
            services.AddSingleton(vaultIface, sp =>
            {
                var existing = TryGetRegisteredVault(sp, modelType, vaultIface);
                if (existing is not null)
                    return existing;

                // Resolve an IKeyspace with the requested name; if none registered, use a lightweight default
                var schemaInstance = sp.GetServices<IKeyspace>()
                    .FirstOrDefault(s => string.Equals(s.Name, schemaName, StringComparison.OrdinalIgnoreCase))
                    ?? new DefaultSchema();

                var sqlProvider = sp.GetRequiredService<ISqlDatabaseProvider>();
                var document = GetDocumentForModel(modelType);

                var pgVaultType = typeof(PgVault<>).MakeGenericType(modelType);
                var instance = Activator.CreateInstance(
                    pgVaultType,
                    sqlProvider,
                    schemaInstance,
                    document,
                    sp
                )!;

                VaultRegistry.RegisterVaultInstance(modelType, instance);
                return instance;
            });
        }
    }

    private static object? TryGetRegisteredVault(IServiceProvider sp, Type modelType, Type vaultIface)
    {
        try
        {
            var reg = VaultRegistry.GetVault(modelType);
            if (reg is not null)
                return reg;
        }
        catch { }
        return null;
    }

    private static async Task BootstrapAsync(IServiceCollection services, Type[] schemaTypes, Type[] vaultModelTypes)
    {
        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<PostgresDBConfiguration>();

        var provider = sp.GetService<ISqlDatabaseProvider>();
        if (provider is null)
        {
            logger.LogWarning("⚠️ No ISqlDatabaseProvider registered; skipping PostgreSQL bootstrap.");
            return;
        }

        if (vaultModelTypes.Length == 0)
        {
            logger.LogInformation("ℹ️ No [Vault]/[Prefab]-annotated IVaultModel types found.");
            return;
        }

        var groups = vaultModelTypes.GroupBy(GetSchemaName);
        var allSchemaInstances = sp.GetServices<IKeyspace>().ToList();

        foreach (var group in groups)
        {
            var schemaName = group.Key;

            var schemaInstance = ResolveSchemaInstance(sp, schemaTypes, allSchemaInstances, schemaName)
                                 ?? new DefaultSchema();

            await provider.ConnectAsync();
            await provider.CreateSchemaAsync(schemaInstance.Name, null);

            await CreateTablesAndRunHooksAsync(provider, sp, logger, schemaInstance, group.ToArray());
            await provider.ShutdownAsync();
        }

        logger.LogInformation("🐘 PostgreSQL activated. {Count} vault model(s) registered and bootstrapped. ✨", vaultModelTypes.Length);
    }

    private static IKeyspace? ResolveSchemaInstance(
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

    // Reuse your existing [Keyspace] attribute (same as Scylla) if you have schema classes.
    private static Type? ResolveSchemaTypeByName(Type[] schemaTypes, string schemaName) =>
        schemaTypes.FirstOrDefault(t =>
        {
            var attr = t.GetCustomAttribute<KeyspaceAttribute>();
            return attr != null && string.Equals(attr.Name, schemaName, StringComparison.OrdinalIgnoreCase);
        });

    private static async Task CreateTablesAndRunHooksAsync(
        ISqlDatabaseProvider provider,
        IServiceProvider sp,
        ILogger<PostgresDBConfiguration> logger,
        IKeyspace schemaInstance,
        Type[] modelTypes)
    {
        foreach (var modelType in modelTypes)
        {
            try
            {
                await provider.CreateTableAsync(modelType, schemaInstance);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to create table for {modelType.Name}. Reason: {ex.Message}");
                continue;
            }

            var instance = modelType.GetConstructor(Type.EmptyTypes)!.Invoke(null) as IVaultModel;

            try
            {
                if (instance is IBeforeVaultCreate before)
                    await before.BeforeCreateAsync(sp);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to run before actions for {modelType.Name}. Reason: {ex.Message}");
            }

            try
            {
                var preloadInterface = instance!
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

                    if (resultObj is IEnumerable resultEnumerable)
                    {
                        int loadedCount = 0;
                        foreach (var _ in resultEnumerable)
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
                                var castMethod = typeof(Enumerable).GetMethod("Cast", BindingFlags.Public | BindingFlags.Static)!
                                    .MakeGenericMethod(modelType);
                                var toListMethod = typeof(Enumerable).GetMethod("ToList", BindingFlags.Public | BindingFlags.Static)!
                                    .MakeGenericMethod(modelType);
                                var typedList = toListMethod.Invoke(null, [castMethod.Invoke(null, new object[] { resultObj })!])!;

                                var saveBatch = vaultIface.GetMethod("SaveBatchAsync", [typeof(IEnumerable<>).MakeGenericType(modelType), typeof(bool?)])
                                               ?? vaultIface.GetMethod("SaveBatchAsync", [typeof(IEnumerable<>).MakeGenericType(modelType)]);

                                object? saveTaskObj;
                                if (saveBatch!.GetParameters().Length == 2)
                                    saveTaskObj = saveBatch.Invoke(vault, [typedList, null]);
                                else
                                    saveTaskObj = saveBatch.Invoke(vault, [typedList]);

                                await ((Task)saveTaskObj!).ConfigureAwait(false);
                                logger.LogInformation($"Streamed {loadedCount} items into {modelType.Name}.");
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to run preload actions for {modelType.Name}. Reason: {ex.Message}");
            }

            try
            {
                if (instance is IAfterVaultCreate after)
                    await after.AfterCreateAsync(sp);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to run after actions for {modelType.Name}. Reason: {ex.Message}");
            }
        }
    }

    private static Document GetDocumentForModel(Type modelType) => Document.From(modelType);

    private static Assembly[] DiscoverAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

    private static IEnumerable<Type> FindSchemaTypes(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesWithAttribute<KeyspaceAttribute>(assemblies)
            .Where(t =>
                t is not null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(IKeyspace).IsAssignableFrom(t))!;

    private static IEnumerable<Type> FindVaultModelTypes(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesWithAttribute<VaultAttribute>(assemblies)
            .Where(t =>
                t is not null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(IVaultModel).IsAssignableFrom(t))!;

    private static string GetSchemaName(Type modelType)
    {
        // Reuse VaultAttribute.Keyspace (Cassandra "keyspace") as Postgres schema name
        var va = modelType.GetCustomAttribute<VaultAttribute>();
        return string.IsNullOrWhiteSpace(va?.Keyspace) ? "public" : va!.Keyspace!;
    }

    [Keyspace("altruist")]
    private sealed class DefaultSchema : IKeyspace
    {
        public string Name { get; set; } = "altruist";

        public IDatabaseServiceToken DatabaseToken => PostgresDBToken.Instance;
    }
}
