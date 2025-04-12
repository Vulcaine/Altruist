
using System.Reflection;
using System.Threading.Tasks;
using Altruist.Contracts;
using Altruist.Database;
using Altruist.UORM;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.ScyllaDB;

public sealed class ScyllaDBConfiguration : IDatabaseConfiguration
{
    public string DatabaseName => "ScyllaDB";

    public void Configure(IServiceCollection services)
    {

    }
}

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

public sealed class ScyllaDBConnectionSetup : DatabaseConnectionSetup<ScyllaDBConnectionSetup>
{

    public ScyllaDBConnectionSetup(IServiceCollection services) : base(services, ScyllaDBToken.Instance)
    {
    }

    public override ScyllaDBConnectionSetup CreateKeyspace<TKeyspace>(
    Func<KeyspaceSetup<TKeyspace>, KeyspaceSetup<TKeyspace>>? setupAction = null)
    {
        if (!typeof(IScyllaKeyspace).IsAssignableFrom(typeof(TKeyspace)))
        {
            throw new InvalidOperationException($"TKeyspace must implement IScyllaKeyspace, but {typeof(TKeyspace).Name} does not.");
        }

        var keyspaceInstance = new TKeyspace();
        var keyspaceName = keyspaceInstance!.Name;

        if (!Keyspaces.TryGetValue(keyspaceName, out var keyspaceSetup))
        {
            keyspaceSetup = (KeyspaceSetup<TKeyspace>)Activator.CreateInstance(
                typeof(ScyllaKeyspaceSetup<>).MakeGenericType(typeof(TKeyspace)),
                _services, keyspaceInstance)!;

            Keyspaces[keyspaceName] = keyspaceSetup;
        }

        setupAction?.Invoke((KeyspaceSetup<TKeyspace>)keyspaceSetup);
        return this;
    }


    public override async Task Build(IAltruistContext settings)
    {
        ILoggerFactory factory = _services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger<ScyllaDBConnectionSetup>();

        if (_contactPoints.Count == 0)
        {
            _contactPoints.Add("localhost:9042");
        }

        _services.AddSingleton<IScyllaDbProvider>(sp => new ScyllaDbProvider(_contactPoints));

        _services.AddSingleton(sp => new ScyllaVaultFactory(sp.GetRequiredService<IScyllaDbProvider>()));
        _services.AddSingleton<IDatabaseVaultFactory>(sp => sp.GetRequiredService<ScyllaVaultFactory>());

        if (Keyspaces.Count == 0)
        {
            await new ScyllaKeyspaceSetup<DefaultScyllaKeyspace>(_services, new DefaultScyllaKeyspace()).Build();
        }
        else
        {
            foreach (var keyspaceSetup in Keyspaces.Values)
            {
                await keyspaceSetup.Build();
            }
        }

        logger.LogInformation("⚡ ScyllaDB support activated. Ready to store and distribute data across realms with incredible speed! 🌌");
    }
}

public class ScyllaKeyspaceSetup<TKeyspace> : KeyspaceSetup<TKeyspace> where TKeyspace : class, IScyllaKeyspace, new()
{
    public ScyllaKeyspaceSetup(IServiceCollection services, TKeyspace instance) : base(services, instance, ScyllaDBToken.Instance)
    {
        services.AddSingleton<IVaultRepository<TKeyspace>>(sp =>
        {
            var provider = sp.GetRequiredService<IScyllaDbProvider>();
            return new ScyllaVaultRepository<TKeyspace>(sp, provider, instance);
        });

        services.AddSingleton(typeof(ScyllaVaultRepository<TKeyspace>), sp => sp.GetRequiredService<IVaultRepository<TKeyspace>>());
    }

    public override async Task Build()
    {
        var builtServices = Services
                .BuildServiceProvider();
        var provider = builtServices
                .GetService<IScyllaDbProvider>();
        var vaultRepo = builtServices
                .GetService<IVaultRepository<TKeyspace>>();


        ILoggerFactory factory = builtServices.GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger<ScyllaKeyspaceSetup<TKeyspace>>();

        if (provider == null || vaultRepo == null)
        {
            throw new InvalidOperationException("ScyllaDB provider is not registered.");
        }

        await provider.ConnectAsync();
        await provider.CreateKeySpaceAsync(Instance.Name, Instance.Options);

        var tableModels = VaultModels.Where(m => m.GetCustomAttribute<VaultAttribute>() != null);
        foreach (var vault in VaultModels)
        {
            await provider.CreateTableAsync(vault, Instance);
            var vaultInstance = vault.GetConstructor(Type.EmptyTypes)!.Invoke(null) as IVaultModel;

            if (vaultInstance is IBeforeVaultCreate before)
            {
                await before.BeforeCreateAsync();
            }

            if (vaultInstance is IOnVaultCreate preload)
            {
                var loaded = await preload.OnCreateAsync();
                if (loaded.Count > 0)
                {
                    await vaultRepo!.Select(vault).SaveBatchAsync(loaded);
                    logger.LogInformation($"Streamed {loaded.Count} items into {vault.Name} vault.");
                }
            }

            if (vaultInstance is IAfterVaultCreate after)
            {
                await after.AfterCreateAsync();
            }
        }
    }
}