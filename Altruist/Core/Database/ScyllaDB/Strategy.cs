
using Altruist.Contracts;
using Altruist.Database;
using Cassandra;
using Microsoft.Extensions.DependencyInjection;

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

    public string Description => "<Database> ScyllaDB";
}

public sealed class ScyllaVaultRepository<TScyllaKeyspace> : VaultRepository<TScyllaKeyspace> where TScyllaKeyspace : class, IScyllaKeyspace
{
    public ScyllaVaultRepository(IServiceProvider provider, IGeneralDatabaseProvider databaseProvider, TScyllaKeyspace keyspace) : base(provider, databaseProvider, keyspace)
    {
    }
}

public sealed class ScyllaDBConnectionSetup : DatabaseConnectionSetup<ScyllaDBConnectionSetup>
{
    private readonly Dictionary<string, IKeyspaceSetup> _keyspaces = new();

    public ScyllaDBConnectionSetup(IServiceCollection services) : base(services)
    {
    }

    public ScyllaDBConnectionSetup CreateKeyspace<TScyllaKeyspace>(Func<KeyspaceSetup<TScyllaKeyspace>, KeyspaceSetup<TScyllaKeyspace>>? setupAction = null) where TScyllaKeyspace : class, IScyllaKeyspace, new()
    {
        var keyspaceInstance = new TScyllaKeyspace();
        var keyspaceName = keyspaceInstance.Name;

        if (!_keyspaces.TryGetValue(keyspaceName, out var keyspaceSetup))
        {
            keyspaceSetup = new KeyspaceSetup<TScyllaKeyspace>(_services, keyspaceInstance);
            _keyspaces[keyspaceName] = keyspaceSetup;
        }

        setupAction?.Invoke((KeyspaceSetup<TScyllaKeyspace>)keyspaceSetup);
        return this;
    }

    public override void Build()
    {
        if (_contactPoints.Count == 0)
        {
            throw new InvalidOperationException("Connection string is not set for ScyllaDB.");
        }

        _services.AddSingleton<IScyllaDbProvider>(sp => new ScyllaDbProvider(_contactPoints));
        _services.AddSingleton(sp => new VaultFactory(sp.GetRequiredService<IScyllaDbProvider>()));

        if (_keyspaces.Count == 0)
        {
            new KeyspaceSetup<DefaultScyllaKeyspace>(_services, new DefaultScyllaKeyspace()).BuildInternal();
        }
        else
        {
            foreach (var keyspaceSetup in _keyspaces.Values)
            {
                keyspaceSetup.BuildInternal();
            }
        }
    }
}


internal interface IKeyspaceSetup
{
    internal void BuildInternal();
}


public sealed class KeyspaceSetup<TKeyspace> : IKeyspaceSetup where TKeyspace : class, IScyllaKeyspace
{
    private readonly IServiceCollection _services;
    private readonly List<Type> _vaultModels = new();
    private readonly TKeyspace _instance;

    internal KeyspaceSetup(IServiceCollection services, TKeyspace instance)
    {
        _services = services;
        _instance = instance;

        _services.AddSingleton<IVaultRepository<TKeyspace>>(sp =>
        {
            var provider = sp.GetRequiredService<IScyllaDbProvider>();
            return new ScyllaVaultRepository<TKeyspace>(sp, provider, _instance);
        });

        _services.AddSingleton(typeof(ScyllaVaultRepository<TKeyspace>), sp => sp.GetRequiredService<IVaultRepository<TKeyspace>>());
    }

    public KeyspaceSetup<TKeyspace> AddVault<TVaultModel>() where TVaultModel : class, IVaultModel
    {
        _vaultModels.Add(typeof(TVaultModel));
        _services.AddSingleton<IVault<TVaultModel>>(sp => new Vault<TVaultModel>(sp.GetRequiredService<VaultFactory>(), _instance));
        return this;
    }

    public void BuildInternal()
    {
        var provider = _services
            .BuildServiceProvider()
            .GetService<IScyllaDbProvider>();

        if (provider == null)
        {
            throw new InvalidOperationException("ScyllaDB provider is not registered.");
        }

        provider.CreateKeySpaceAsync(_instance.Name, _instance.Options);
        foreach (var vault in _vaultModels)
        {
            provider.CreateTableAsync(vault, _instance);
        }
    }
}
