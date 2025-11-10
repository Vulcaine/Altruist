/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

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

public sealed class ScyllaVaultRepository<TScyllaKeyspace> : VaultRepository<TScyllaKeyspace> where TScyllaKeyspace : class, IScyllaKeyspace
{
    public ScyllaVaultRepository(IServiceProvider provider, IScyllaDbProvider databaseProvider, TScyllaKeyspace keyspace) : base(provider, databaseProvider, keyspace)
    {
    }
}

// [Configuration]
public sealed class ScyllaDBKeyspaceConfiguration : IAltruistConfiguration
{
    private readonly ILogger<ScyllaDBKeyspaceConfiguration> _logger;
    private readonly IConfiguration _cfg;
    private readonly IScyllaDbProvider _provider; // autowired (non-generic dep)

    // Non-generic dependencies can be autowired via ctor.
    public ScyllaDBKeyspaceConfiguration(
        ILogger<ScyllaDBKeyspaceConfiguration> logger,
        IConfiguration cfg,
        IScyllaDbProvider provider)
    {
        _logger = logger;
        _cfg = cfg;
        _provider = provider;
    }

    public async Task Configure(IServiceCollection services)
    {
        var keyspaceTypes = FindKeyspaceTypes().ToArray();

        foreach (var ksType in keyspaceTypes)
        {
            services.AddSingleton(ksType, sp =>
            {
                var inst = DependencyResolver.CreateWithConfiguration(sp, _cfg, ksType, _logger);
                DependencyResolver.InvokePostConstruct(inst, sp, _cfg, _logger);
                return inst!;
            });


            var ivaultRepoType = typeof(IVaultRepository<>).MakeGenericType(ksType);
            var scyllaRepoType = typeof(ScyllaVaultRepository<>).MakeGenericType(ksType);

            services.AddSingleton(ivaultRepoType, sp =>
            {
                var provider = sp.GetRequiredService<IScyllaDbProvider>();
                var instance = sp.GetRequiredService(ksType);
                var repo = Activator.CreateInstance(scyllaRepoType, sp, provider, instance);
                return repo!;
            });

            services.AddSingleton(scyllaRepoType, sp => sp.GetRequiredService(ivaultRepoType));

            _logger.LogInformation("🔧 Keyspace wired: {Keyspace} ⇒ IVaultRepository<{Keyspace}> (Singleton)",
                DependencyResolver.GetCleanName(ksType), DependencyResolver.GetCleanName(ksType));
        }


        using var builtServices = services.BuildServiceProvider();

        var loggerFactory = builtServices.GetRequiredService<ILoggerFactory>();
        var initLogger = loggerFactory.CreateLogger<ScyllaDBKeyspaceConfiguration>();

        var provider = builtServices.GetService<IScyllaDbProvider>() ?? _provider;
        if (provider == null)
            throw new InvalidOperationException("ScyllaDB provider is not registered.");

        var vaultModels = FindVaultModels().ToArray(); // scan once

        foreach (var ksType in keyspaceTypes)
        {
            // Resolve strongly-typed pieces
            var ksInstance = (IScyllaKeyspace)builtServices.GetRequiredService(ksType);
            var ivaultRepoType = typeof(IVaultRepository<>).MakeGenericType(ksType);
            dynamic vaultRepo = builtServices.GetRequiredService(ivaultRepoType);

            // ----- original flow (no extra reflection) -----

            await provider.ConnectAsync();
            await provider.CreateKeySpaceAsync(ksInstance.Name, ksInstance.Options);

            var tableModels = vaultModels.Where(m => m.GetCustomAttribute<VaultAttribute>() != null);
            foreach (var vault in tableModels)
            {
                try
                {
                    await provider.CreateTableAsync(vault, ksInstance);
                }
                catch (Exception ex)
                {
                    initLogger.LogError(ex, $"Failed to create table for {vault.Name}. Reason: {ex.Message}");
                    continue;
                }

                // Create a vault instance for hooks
                var vaultInstance = vault.GetConstructor(Type.EmptyTypes)!.Invoke(null) as IVaultModel;

                // BeforeCreate
                try
                {
                    if (vaultInstance is IBeforeVaultCreate before)
                    {
                        await before.BeforeCreateAsync(builtServices);
                    }
                }
                catch (Exception ex)
                {
                    initLogger.LogError(ex, $"Failed to run before actions for {vault.Name}. Reason: {ex.Message}");
                }

                // OnCreate preload (typed calls, no method reflection)
                try
                {
                    if (vaultInstance is IOnVaultCreate preload)
                    {
                        var loaded = await preload.OnCreateAsync(builtServices);
                        if (loaded.Count > 0)
                        {
                            var remoteVault = vaultRepo.Select(vault);
                            var count = await remoteVault.CountAsync();

                            if (count == 0)
                            {
                                await vaultRepo.Select(vault).SaveBatchAsync(loaded);
                                initLogger.LogInformation($"Streamed {loaded.Count} items into {vault.Name} vault.");
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    initLogger.LogError(ex, $"Failed to run preload actions for {vault.Name}. Reason: {ex.Message}");
                }

                // AfterCreate
                try
                {
                    if (vaultInstance is IAfterVaultCreate after)
                    {
                        await after.AfterCreateAsync(builtServices);
                    }
                }
                catch (Exception ex)
                {
                    initLogger.LogError(ex, $"Failed to run after actions for {vault.Name}. Reason: {ex.Message}");
                }
            }

            await provider.ShutdownAsync();
        }
    }

    private static IEnumerable<Type> FindKeyspaceTypes()
    {
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .SelectMany(a =>
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
    }

    private static IEnumerable<Type> FindVaultModels()
    {
        return AppDomain.CurrentDomain
            .GetAssemblies()
            .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
            .SelectMany(a =>
            {
                try { return a.GetTypes(); }
                catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null)!; }
            })
            .Where(t =>
                t is not null &&
                t.IsClass &&
                !t.IsAbstract &&
                typeof(IVaultModel).IsAssignableFrom(t) &&
                t.GetCustomAttribute<VaultAttribute>() is not null)!;
    }
}