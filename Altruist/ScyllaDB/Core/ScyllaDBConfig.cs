/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Reflection;
using Altruist.Contracts;
using Altruist.Persistence;
using Altruist.UORM;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.ScyllaDB;

/// <summary>
/// Simple database bootstrapper: discovers all vault models ([Vault]) and ensures
/// their ScyllaDB tables exist. Runs optional lifecycle hooks:
/// IBeforeVaultCreate, IOnVaultCreate (preload), IAfterVaultCreate.
///
/// It registers IVaultRepository&lt;TKeyspace&gt; and ScyllaVaultRepository&lt;TKeyspace&gt; for each
/// discovered keyspace type <see cref="IScyllaKeyspace"/>, then performs the bootstrap.
/// </summary>
[Configuration]
public sealed class ScyllaDBConfiguration : IDatabaseConfiguration
{
    public string DatabaseName => "ScyllaDB";

    public async Task Configure(IServiceCollection services)
    {
        var cfg = AppConfigLoader.Load();

        var assemblies = AppDomain.CurrentDomain.GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .ToArray();

        var keyspaceTypes = assemblies
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
            })
            .Where(t => t is not null && t.IsClass && !t.IsAbstract && typeof(IScyllaKeyspace).IsAssignableFrom(t))
            .Distinct()
            .ToArray();

        var vaultTypes = TypeDiscovery.FindTypesWithAttribute<VaultAttribute>(assemblies)
            .Where(t => t is not null && t.IsClass && !t.IsAbstract && typeof(IVaultModel).IsAssignableFrom(t))
            .Distinct()
            .ToArray();

        foreach (var ksType in keyspaceTypes)
        {
            services.AddSingleton(ksType, sp =>
            {
                var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ScyllaDBConfiguration>();
                var instance = DependencyResolver.CreateWithConfiguration(sp, cfg, ksType, logger);
                DependencyResolver.InvokePostConstruct(instance, sp, cfg, logger);
                return instance!;
            });

            var iVaultRepoType = typeof(IVaultRepository<>).MakeGenericType(ksType);
            var scyllaRepoType = typeof(ScyllaVaultRepository<>).MakeGenericType(ksType);

            services.AddSingleton(iVaultRepoType, sp =>
            {
                var provider = sp.GetRequiredService<IScyllaDbProvider>();
                var instance = sp.GetRequiredService(ksType);
                // new ScyllaVaultRepository<TKeyspace>(sp, provider, instance)
                return Activator.CreateInstance(scyllaRepoType, sp, provider, instance)!;
            });

            services.AddSingleton(scyllaRepoType, sp => sp.GetRequiredService(iVaultRepoType));
        }

        // ---- 3) Build provider AFTER registrations, then run bootstrap ----
        using var sp = services.BuildServiceProvider();
        var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ScyllaDBConfiguration>();

        var provider = sp.GetService<IScyllaDbProvider>();
        if (provider is null)
        {
            logger.LogWarning("⚠️ No IScyllaDbProvider registered; skipping ScyllaDB bootstrap.");
            return;
        }

        if (vaultTypes.Length == 0)
        {
            logger.LogInformation("ℹ️ No [Vault]-annotated IVaultModel types found.");
            return;
        }

        // Group vaults by keyspace name from [Vault(Keyspace=...)] (default 'altruist')
        var byKeyspace = vaultTypes.GroupBy(t =>
        {
            var va = t.GetCustomAttribute<VaultAttribute>();
            return string.IsNullOrWhiteSpace(va?.Keyspace) ? "altruist" : va!.Keyspace!;
        });

        // Resolve all keyspace instances (already registered above)
        var allKeyspaces = sp.GetServices<IKeyspace>().OfType<IScyllaKeyspace>().ToList();

        foreach (var group in byKeyspace)
        {
            var keyspaceName = group.Key;

            var ksInstance = allKeyspaces.FirstOrDefault(k =>
                string.Equals(k.Name, keyspaceName, StringComparison.OrdinalIgnoreCase));

            if (ksInstance is null)
            {
                // Try to instantiate a matching keyspace type on the fly (rare if not registered already)
                var ksType = keyspaceTypes.FirstOrDefault(t =>
                {
                    try
                    {
                        var tmp = (IScyllaKeyspace)ActivatorUtilities.CreateInstance(sp, t);
                        return string.Equals(tmp.Name, keyspaceName, StringComparison.OrdinalIgnoreCase);
                    }
                    catch { return false; }
                });

                if (ksType != null)
                {
                    ksInstance = (IScyllaKeyspace)sp.GetRequiredService(ksType);
                }
            }

            if (ksInstance is null)
            {
                logger.LogWarning("⚠️ Keyspace '{Keyspace}' could not be resolved; skipping its vaults.", keyspaceName);
                continue;
            }

            // Get the typed repository service we registered above
            var repoServiceType = typeof(IVaultRepository<>).MakeGenericType(ksInstance.GetType());
            dynamic vaultRepo = sp.GetRequiredService(repoServiceType);

            // ---- Original flow (minimal reflection identical to your Build()) ----

            await provider.ConnectAsync();
            await provider.CreateKeySpaceAsync(ksInstance.Name, ksInstance.Options);

            var tableModels = group.Where(m => m.GetCustomAttribute<VaultAttribute>() != null);
            foreach (var vault in tableModels)
            {
                try
                {
                    await provider.CreateTableAsync(vault, ksInstance);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Failed to create table for {vault.Name}. Reason: {ex.Message}");
                    continue;
                }

                // Instantiate model to run hooks
                var vaultInstance = vault.GetConstructor(Type.EmptyTypes)!.Invoke(null) as IVaultModel;

                // BeforeCreate
                try
                {
                    if (vaultInstance is IBeforeVaultCreate before)
                        await before.BeforeCreateAsync(sp);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Failed to run before actions for {vault.Name}. Reason: {ex.Message}");
                }

                // OnCreate preload
                try
                {
                    if (vaultInstance is IOnVaultCreate preload)
                    {
                        var loaded = await preload.OnCreateAsync(sp) ?? new List<IVaultModel>();
                        if (loaded.Count > 0)
                        {
                            var remoteVault = vaultRepo.Select(vault);
                            var count = await remoteVault.CountAsync();

                            if (count == 0)
                            {
                                await vaultRepo.Select(vault).SaveBatchAsync(loaded);
                                logger.LogInformation($"Streamed {loaded.Count} items into {vault.Name} vault.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Failed to run preload actions for {vault.Name}. Reason: {ex.Message}");
                }

                // AfterCreate
                try
                {
                    if (vaultInstance is IAfterVaultCreate after)
                        await after.AfterCreateAsync(sp);
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, $"Failed to run after actions for {vault.Name}. Reason: {ex.Message}");
                }
            }

            await provider.ShutdownAsync();
        }

        logger.LogInformation("⚡ ScyllaDB support activated. Ready to store and distribute data across realms with incredible speed! 🌌");
    }
}
