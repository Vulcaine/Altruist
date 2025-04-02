using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Database;

public class ReplicationOptions
{
    public int ReplicationFactor { get; set; } = 1;

    /// <summary>
    /// Defines replication per data center (Only used for NetworkTopologyStrategy).
    /// The key is the Data Center name (configured in ScyllaDB).
    /// </summary>
    public Dictionary<string, int>? DataCenters { get; set; }
}

public abstract class KeyspaceSetup<TKeyspace> : IKeyspaceSetup where TKeyspace : class, IKeyspace
{
    protected readonly IServiceCollection Services;
    protected readonly List<Type> VaultModels = new();
    protected readonly TKeyspace Instance;

    public IDatabaseServiceToken Token { get; }

    internal KeyspaceSetup(IServiceCollection services, TKeyspace instance, IDatabaseServiceToken token)
    {
        Services = services;
        Instance = instance;
        Token = token;
    }


    public KeyspaceSetup<TKeyspace> AddVault<TVaultModel>() where TVaultModel : class, IVaultModel
    {
        VaultModels.Add(typeof(TVaultModel));
        Services.AddSingleton<IVault<TVaultModel>>(sp => new Vault<TVaultModel>(sp.GetServices<IVaultFactory>().Where(s => s.Token == Token).First(), Instance));
        return this;
    }

    public abstract void Build();
}

public interface IKeyspaceSetup
{
    IDatabaseServiceToken Token { get; }
    void Build();
}

public class VaultRepositoryFactory
{
    private readonly IServiceProvider _provider;

    public VaultRepositoryFactory(IServiceProvider provider)
    {
        _provider = provider;
    }

    public IVaultRepository<TKeyspace> Make<TKeyspace>(IDatabaseServiceToken token) where TKeyspace : class, IKeyspace, new() => _provider.GetServices<IVaultRepository<TKeyspace>>().Where(p => p.Token == token).First();
}


public class DatabaseProviderFactory
{
    private readonly IServiceProvider _provider;

    public DatabaseProviderFactory(IServiceProvider provider)
    {
        _provider = provider;
    }

    public IGeneralDatabaseProvider Make(IDatabaseServiceToken token) => _provider.GetServices<IGeneralDatabaseProvider>().Where(p => p.Token == token).First();
}