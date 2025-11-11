/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Collections;
using System.Linq;
using System.Reflection;
using Altruist.Contracts;
using Altruist.Persistence;
using Altruist.UORM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.ScyllaDB;

public sealed class ScyllaDBToken : IDatabaseServiceToken
{
    public static ScyllaDBToken Instance { get; } = new ScyllaDBToken();
    public IDatabaseConfiguration Configuration => new ScyllaDBConfiguration();
    public string Description => "💾 Database: ScyllaDB";
}

// NOTE: Present but unused per your instruction to avoid repos entirely.
public sealed class ScyllaVaultRepository<TScyllaKeyspace> : VaultRepository<TScyllaKeyspace>
    where TScyllaKeyspace : class, IScyllaKeyspace
{
    public ScyllaVaultRepository(IServiceProvider provider, IScyllaDbProvider databaseProvider, TScyllaKeyspace keyspace)
        : base(provider, databaseProvider, keyspace) { }
}

[Configuration]
public sealed class ScyllaDBConfiguration : IDatabaseConfiguration
{
    public string DatabaseName => "ScyllaDB";

    public async Task Configure(IServiceCollection services)
    {
        var cfg = AppConfigLoader.Load();
        var assemblies = DiscoverAssemblies();

        var keyspaceTypes = FindKeyspaceTypes(assemblies).ToArray();
        var vaultModelTypes = FindVaultModelTypes(assemblies).ToArray();

        RegisterKeyspaces(services, cfg, keyspaceTypes);
        RegisterVaultsAndPopulateRegistry(services, keyspaceTypes, vaultModelTypes);

        await BootstrapAsync(services, keyspaceTypes, vaultModelTypes);
    }

    // ----------------------- Registration -----------------------

    private static void RegisterKeyspaces(IServiceCollection services, IConfiguration cfg, Type[] keyspaceTypes)
    {
        foreach (var ksType in keyspaceTypes)
        {
            // Concrete singleton
            services.AddSingleton(ksType, sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ScyllaDBConfiguration>();
                var inst = DependencyResolver.CreateWithConfiguration(sp, cfg, ksType, logger);
                _ = DependencyResolver.InvokePostConstructAsync(inst, sp, cfg, logger);
                return inst!;
            });

            // Expose as IKeyspace & IScyllaKeyspace for discovery
            services.AddSingleton(typeof(IKeyspace), sp => (IKeyspace)sp.GetRequiredService(ksType));
            services.AddSingleton(typeof(IScyllaKeyspace), sp => (IScyllaKeyspace)sp.GetRequiredService(ksType));
        }
    }

    private static void RegisterVaultsAndPopulateRegistry(
        IServiceCollection services,
        Type[] keyspaceTypes,
        Type[] vaultModelTypes)
    {
        foreach (var modelType in vaultModelTypes)
        {
            var ksName = GetKeyspaceName(modelType);

            // Populate VaultRegistry for hot paths
            Altruist.VaultRegistry.Register(modelType, ksName);

            // IVault<TModel> → CqlVault<TModel> (constructed manually; no repos)
            var vaultIface = typeof(IVault<>).MakeGenericType(modelType);
            services.AddTransient(vaultIface, sp =>
            {
                // Resolve the target keyspace instance by name
                var ksInstance = sp.GetServices<IKeyspace>()
                    .OfType<IScyllaKeyspace>()
                    .FirstOrDefault(k => string.Equals(k.Name, ksName, StringComparison.OrdinalIgnoreCase));

                if (ksInstance is null)
                {
                    var ksType = ResolveKeyspaceTypeByName(keyspaceTypes, ksName)
                        ?? throw new InvalidOperationException($"Keyspace '{ksName}' for model '{modelType.Name}' is not registered.");
                    ksInstance = (IScyllaKeyspace)sp.GetRequiredService(ksType);
                }

                // Resolve CQL provider (IScyllaDbProvider should implement ICqlDatabaseProvider)
                var cqlProvider =
                    sp.GetService<ICqlDatabaseProvider>() ??
                    (ICqlDatabaseProvider)sp.GetRequiredService<IScyllaDbProvider>();

                // Build Document for the model type (no per-op reflection)
                var document = GetDocumentForModel(modelType);

                // Create CqlVault<TModel> via reflection once at resolution
                var cqlVaultType = typeof(CqlVault<>).MakeGenericType(modelType);
                var instance = Activator.CreateInstance(
                    cqlVaultType,
                    cqlProvider,               // ICqlDatabaseProvider
                    ksInstance,                // IKeyspace
                    document,                  // Document
                    sp                         // IServiceProvider
                );

                return instance!;
            });
        }
    }

    // ----------------------- Bootstrap (create + hooks) -----------------------

    private static async Task BootstrapAsync(IServiceCollection services, Type[] keyspaceTypes, Type[] vaultModelTypes)
    {
        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ScyllaDBConfiguration>();

        var provider = sp.GetService<IScyllaDbProvider>();
        if (provider is null)
        {
            logger.LogWarning("⚠️ No IScyllaDbProvider registered; skipping ScyllaDB bootstrap.");
            return;
        }

        if (vaultModelTypes.Length == 0)
        {
            logger.LogInformation("ℹ️ No [Vault]/[Prefab]-annotated IVaultModel types found.");
            return;
        }

        var groups = vaultModelTypes.GroupBy(GetKeyspaceName);
        var allKsInstances = sp.GetServices<IKeyspace>().OfType<IScyllaKeyspace>().ToList();

        foreach (var group in groups)
        {
            var keyspaceName = group.Key;

            var ksInstance = ResolveKeyspaceInstance(sp, keyspaceTypes, allKsInstances, keyspaceName);
            if (ksInstance is null)
            {
                logger.LogWarning("⚠️ Keyspace '{Keyspace}' could not be resolved; skipping its vaults/prefabs.", keyspaceName);
                continue;
            }

            await provider.ConnectAsync();
            await provider.CreateKeySpaceAsync(ksInstance.Name, ksInstance.Options);

            await CreateTablesAndRunHooksAsync(provider, sp, logger, ksInstance, group.ToArray());
            await provider.ShutdownAsync();
        }

        logger.LogInformation("⚡ ScyllaDB activated. {Count} vault model(s) registered and bootstrapped. 🌌", vaultModelTypes.Length);
    }

    private static IScyllaKeyspace? ResolveKeyspaceInstance(
        IServiceProvider sp,
        Type[] keyspaceTypes,
        List<IScyllaKeyspace> allKsInstances,
        string keyspaceName)
    {
        var ksInstance = allKsInstances.FirstOrDefault(k =>
            string.Equals(k.Name, keyspaceName, StringComparison.OrdinalIgnoreCase));

        if (ksInstance is null)
        {
            var ksType = ResolveKeyspaceTypeByName(keyspaceTypes, keyspaceName);
            if (ksType is not null)
                ksInstance = (IScyllaKeyspace)sp.GetRequiredService(ksType);
        }

        return ksInstance;
    }

    private static Type? ResolveKeyspaceTypeByName(Type[] keyspaceTypes, string keyspaceName) =>
        keyspaceTypes.FirstOrDefault(t =>
        {
            var attr = t.GetCustomAttribute<KeyspaceAttribute>();
            return attr != null && string.Equals(attr.Name, keyspaceName, StringComparison.OrdinalIgnoreCase);
        });

    private static async Task CreateTablesAndRunHooksAsync(
        IScyllaDbProvider provider,
        IServiceProvider sp,
        ILogger<ScyllaDBConfiguration> logger,
        IScyllaKeyspace ksInstance,
        Type[] modelTypes)
    {
        foreach (var modelType in modelTypes)
        {
            // Create table
            try
            {
                await provider.CreateTableAsync(modelType, ksInstance);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to create table for {modelType.Name}. Reason: {ex.Message}");
                continue;
            }

            var instance = modelType.GetConstructor(Type.EmptyTypes)!.Invoke(null) as IVaultModel;

            // BeforeCreate
            try
            {
                if (instance is IBeforeVaultCreate before)
                    await before.BeforeCreateAsync(sp);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, $"Failed to run before actions for {modelType.Name}. Reason: {ex.Message}");
            }

            // OnCreate preload (generic IOnVaultCreate<T>)
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

                    var resultObj = taskObj.GetType().GetProperty("Result")!.GetValue(taskObj);
                    if (resultObj is IEnumerable result)
                    {
                        int loadedCount = 0;
                        foreach (var _ in result) loadedCount++;

                        if (loadedCount > 0)
                        {
                            // Resolve IVault<TModel> directly (CqlVault<TModel> from DI)
                            var vaultIface = typeof(IVault<>).MakeGenericType(modelType);
                            var remoteVaultObj = sp.GetRequiredService(vaultIface);

                            // Use the type-erased surface to avoid dynamic binder issues
                            var typeErased = (ITypeErasedVault)remoteVaultObj;

                            var count = await typeErased.CountAsync();
                            if (count == 0)
                            {
                                // Convert result to IEnumerable<object> for the type-erased overload
                                var payload = result.Cast<object>().ToList();
                                await typeErased.SaveBatchAsync(payload);
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

            // AfterCreate
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

    // ----------------------- Helpers -----------------------

    private static Document GetDocumentForModel(Type modelType)
    {
        // Prefer a generic Document.For<T>() if present
        var forGeneric = typeof(Document).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "For" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        if (forGeneric is not null)
        {
            var closed = forGeneric.MakeGenericMethod(modelType);
            var doc = closed.Invoke(null, null);
            if (doc is Document d1) return d1;
        }

        // Try a non-generic overload: Document.For(Type) or Document.From(Type)
        var forType = typeof(Document).GetMethod("For", BindingFlags.Public | BindingFlags.Static, new[] { typeof(Type) })
                   ?? typeof(Document).GetMethod("From", BindingFlags.Public | BindingFlags.Static, new[] { typeof(Type) });
        if (forType is not null)
        {
            var doc = forType.Invoke(null, new object[] { modelType });
            if (doc is Document d2) return d2;
        }

        throw new InvalidOperationException($"Document builder not found for model '{modelType.Name}'. Expected Document.For<T>() or Document.For(Type).");
    }

    private static Assembly[] DiscoverAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

    private static IEnumerable<Type> FindKeyspaceTypes(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesWithAttribute<KeyspaceAttribute>(assemblies)
            .Where(t =>
                t is not null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(IScyllaKeyspace).IsAssignableFrom(t))!;

    private static IEnumerable<Type> FindVaultModelTypes(Assembly[] assemblies) =>
        TypeDiscovery.FindTypesWithAttribute<VaultAttribute>(assemblies)
            .Where(t =>
                t is not null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(IVaultModel).IsAssignableFrom(t))!;

    private static string GetKeyspaceName(Type modelType)
    {
        var va = modelType.GetCustomAttribute<VaultAttribute>();
        return string.IsNullOrWhiteSpace(va?.Keyspace) ? "altruist" : va!.Keyspace!;
    }
}
