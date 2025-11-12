/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Collections;
using System.Reflection;
using Altruist;
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

    private static void RegisterKeyspaces(IServiceCollection services, IConfiguration cfg, Type[] keyspaceTypes)
    {
        foreach (var ksType in keyspaceTypes)
        {
            services.AddSingleton(ksType, sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ScyllaDBConfiguration>();
                var inst = DependencyResolver.CreateWithConfiguration(sp, cfg, ksType, logger);
                _ = DependencyResolver.InvokePostConstructAsync(inst, sp, cfg, logger);
                return inst!;
            });

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

            VaultRegistry.Register(modelType, ksName);

            var vaultIface = typeof(IVault<>).MakeGenericType(modelType);
            services.AddSingleton(vaultIface, sp =>
            {
                var existing = TryGetRegisteredVault(sp, modelType, vaultIface);
                if (existing is not null) return existing;

                var ksInstance = sp.GetServices<IKeyspace>()
                    .OfType<IScyllaKeyspace>()
                    .FirstOrDefault(k => string.Equals(k.Name, ksName, StringComparison.OrdinalIgnoreCase));

                if (ksInstance is null)
                {
                    var ksType = ResolveKeyspaceTypeByName(keyspaceTypes, ksName)
                        ?? throw new InvalidOperationException($"Keyspace '{ksName}' for model '{modelType.Name}' is not registered.");
                    ksInstance = (IScyllaKeyspace)sp.GetRequiredService(ksType);
                }

                var cqlProvider =
                    sp.GetService<ICqlDatabaseProvider>() ??
                    (ICqlDatabaseProvider)sp.GetRequiredService<IScyllaDbProvider>();

                var document = GetDocumentForModel(modelType);

                var cqlVaultType = typeof(CqlVault<>).MakeGenericType(modelType);
                var instance = Activator.CreateInstance(
                    cqlVaultType,
                    cqlProvider,
                    ksInstance,
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
            if (reg is not null) return reg;
        }
        catch { }
        return null;
    }

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
                        foreach (var _ in resultEnumerable) loadedCount++;

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
                                var typedList = toListMethod.Invoke(null, new object[] { castMethod.Invoke(null, new object[] { resultObj })! })!;

                                var saveBatch = vaultIface.GetMethod("SaveBatchAsync", new[] { typeof(IEnumerable<>).MakeGenericType(modelType), typeof(bool?) })
                                               ?? vaultIface.GetMethod("SaveBatchAsync", new[] { typeof(IEnumerable<>).MakeGenericType(modelType) });

                                object? saveTaskObj;
                                if (saveBatch!.GetParameters().Length == 2)
                                    saveTaskObj = saveBatch.Invoke(vault, new object?[] { typedList, null });
                                else
                                    saveTaskObj = saveBatch.Invoke(vault, new object?[] { typedList });

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

    private static Document GetDocumentForModel(Type modelType)
    {
        var forGeneric = typeof(Document).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .FirstOrDefault(m => m.Name == "For" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
        if (forGeneric is not null)
        {
            var closed = forGeneric.MakeGenericMethod(modelType);
            var doc = closed.Invoke(null, null);
            if (doc is Document d1) return d1;
        }

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
