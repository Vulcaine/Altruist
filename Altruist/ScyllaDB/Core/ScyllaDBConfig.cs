/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
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

/// <summary>
/// Single ScyllaDB bootstrapper:
/// - Discovers all keyspaces (<see cref="IScyllaKeyspace"/> with [Keyspace]) and vault models ([Vault]/[Prefab] &amp; <see cref="IVaultModel"/>)
/// - Registers:
///     * Keyspace singletons
///     * IVaultRepository&lt;TKeyspace&gt; → ScyllaVaultRepository&lt;TKeyspace&gt;
///     * For each vault model T: IVault&lt;T&gt; (resolved via the proper keyspace repository)
/// - Creates keyspaces/tables and runs lifecycle hooks:
///     IBeforeVaultCreate, IOnVaultCreate&lt;T&gt;, IAfterVaultCreate
/// </summary>
[Configuration]
public sealed class ScyllaDBConfiguration : IDatabaseConfiguration
{
    public string DatabaseName => "ScyllaDB";

    // ----------------------- Public entrypoint -----------------------
    public async Task Configure(IServiceCollection services)
    {
        var cfg = AppConfigLoader.Load();
        var assemblies = DiscoverAssemblies();

        var keyspaceTypes = FindKeyspaceTypes(assemblies).ToArray();
        var vaultModelTypes = FindVaultModelTypes(assemblies).ToArray();

        RegisterKeyspacesAndRepositories(services, cfg, keyspaceTypes);
        RegisterVaultsAndPopulateRegistry(services, vaultModelTypes);

        await BootstrapAsync(services, keyspaceTypes, vaultModelTypes);
    }

    // ----------------------- Registration steps -----------------------
    private static void RegisterKeyspacesAndRepositories(IServiceCollection services, IConfiguration cfg, Type[] keyspaceTypes)
    {
        foreach (var ksType in keyspaceTypes)
        {
            services.AddSingleton(ksType, sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ScyllaDBConfiguration>();
                var instance = DependencyResolver.CreateWithConfiguration(sp, cfg, ksType, logger);
                _ = DependencyResolver.InvokePostConstructAsync(instance, sp, cfg, logger);
                return instance!;
            });

            var iVaultRepoType = typeof(IVaultRepository<>).MakeGenericType(ksType);
            var scyllaRepoType = typeof(ScyllaVaultRepository<>).MakeGenericType(ksType);

            services.AddSingleton(iVaultRepoType, sp =>
            {
                var provider = sp.GetRequiredService<IScyllaDbProvider>();
                var ksInstance = sp.GetRequiredService(ksType);
                return Activator.CreateInstance(scyllaRepoType, sp, provider, ksInstance)!;
            });

            // Also expose the concrete repo type if requested
            services.AddSingleton(scyllaRepoType, sp => sp.GetRequiredService(iVaultRepoType));
        }
    }

    private static void RegisterVaultsAndPopulateRegistry(IServiceCollection services, Type[] vaultModelTypes)
    {
        foreach (var modelType in vaultModelTypes)
        {
            var ksName = GetKeyspaceName(modelType);

            // 1) Populate global registry
            Altruist.VaultRegistry.Register(modelType, ksName);

            // 2) Register IVault<TModel> resolving through the correct keyspace repository
            var vaultIface = typeof(IVault<>).MakeGenericType(modelType);
            services.AddTransient(vaultIface, sp =>
            {
                var allKeyspaces = sp.GetServices<IKeyspace>().OfType<IScyllaKeyspace>().ToList();

                var ksInstance = allKeyspaces.FirstOrDefault(k =>
                    string.Equals(k.Name, ksName, StringComparison.OrdinalIgnoreCase));

                if (ksInstance is null)
                    throw new InvalidOperationException(
                        $"Keyspace '{ksName}' for model '{modelType.Name}' is not registered.");

                var repoServiceType = typeof(IVaultRepository<>).MakeGenericType(ksInstance.GetType());
                dynamic repo = sp.GetRequiredService(repoServiceType);

                // Returns IVault<TModel> (runtime generic)
                return repo.Select(modelType);
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

            var repoServiceType = typeof(IVaultRepository<>).MakeGenericType(ksInstance.GetType());
            dynamic vaultRepo = sp.GetRequiredService(repoServiceType);

            await provider.ConnectAsync();
            await provider.CreateKeySpaceAsync(ksInstance.Name, ksInstance.Options);

            await CreateTablesAndRunHooksAsync(provider, sp, logger, vaultRepo, ksInstance, group.ToArray());
            await provider.ShutdownAsync();
        }

        var registeredCount = vaultModelTypes.Length;
        if (registeredCount > 0)
            logger.LogInformation("⚡ ScyllaDB activated. {Count} vault model(s) registered and bootstrapped. 🌌", registeredCount);
        else
            logger.LogInformation("⚡ ScyllaDB activated. No vault models discovered. 🌌");
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
            var ksType = keyspaceTypes.FirstOrDefault(t =>
            {
                try
                {
                    var tmp = (IScyllaKeyspace)ActivatorUtilities.CreateInstance(sp, t);
                    return string.Equals(tmp.Name, keyspaceName, StringComparison.OrdinalIgnoreCase);
                }
                catch { return false; }
            });

            if (ksType is not null)
                ksInstance = (IScyllaKeyspace)sp.GetRequiredService(ksType);
        }

        return ksInstance;
    }

    private static async Task CreateTablesAndRunHooksAsync(
        IScyllaDbProvider provider,
        IServiceProvider sp,
        ILogger<ScyllaDBConfiguration> logger,
        dynamic vaultRepo,
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

            // OnCreate preload (generic IOnVaultCreate<T> via reflection shim)
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

                    var result = taskObj.GetType().GetProperty("Result")!.GetValue(taskObj)!;

                    int loadedCount = 0;
                    foreach (var _ in (IEnumerable)result) loadedCount++;

                    if (loadedCount > 0)
                    {
                        var remoteVault = vaultRepo.Select(modelType);
                        var count = await remoteVault.CountAsync();

                        if (count == 0)
                        {
                            await remoteVault.SaveBatchAsync(result);
                            logger.LogInformation($"Streamed {loadedCount} items into {modelType.Name}.");
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

    // ----------------------- Discovery helpers -----------------------
    private static Assembly[] DiscoverAssemblies() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

    private static IEnumerable<Type> FindKeyspaceTypes(Assembly[] assemblies) =>
        assemblies.SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
            })
            .Where(t =>
                t is not null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(IScyllaKeyspace).IsAssignableFrom(t) &&
                t.GetCustomAttribute<KeyspaceAttribute>() is not null)!;

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
