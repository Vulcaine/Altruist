using System.Reflection;
using Altruist.Contracts;
using Altruist.UORM;
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

    public KeyspaceSetup(IServiceCollection services, TKeyspace instance, IDatabaseServiceToken token)
    {
        Services = services;
        Instance = instance;
        Token = token;
    }


    public KeyspaceSetup<TKeyspace> AddVault<TVaultModel>() where TVaultModel : class, IVaultModel
    {
        VaultModels.Add(typeof(TVaultModel));
        Services.AddSingleton<IVault<TVaultModel>>(sp => new VaultAdapter<TVaultModel>(sp.GetServices<IDatabaseVaultFactory>().Where(s => s.Token == Token).First(), Instance));
        return this;
    }

    public KeyspaceSetup<TKeyspace> AddVault(Type vault)
    {
        if (!typeof(IVaultModel).IsAssignableFrom(vault))
            throw new ArgumentException($"{vault.FullName} must implement IVaultModel");

        var attribute = vault.GetCustomAttribute<VaultAttribute>();
        var keyspaceFromAttribute = attribute?.Keyspace ?? "altruist";
        var currentKeyspaceName = Instance.Name;

        if (!string.Equals(keyspaceFromAttribute, currentKeyspaceName, StringComparison.OrdinalIgnoreCase))
        {
            // Keyspace mismatch, skip registration
            return this;
        }

        VaultModels.Add(vault);

        var vaultInterfaceType = typeof(IVault<>).MakeGenericType(vault);
        var vaultImplementationType = typeof(VaultAdapter<>).MakeGenericType(vault);

        Services.AddSingleton(vaultInterfaceType, sp =>
        {
            var factory = sp.GetServices<IDatabaseVaultFactory>().First(s => s.Token == Token);
            return Activator.CreateInstance(vaultImplementationType, factory, Instance)!;
        });

        Services.AddSingleton(vaultImplementationType, sp => sp.GetRequiredService(vaultInterfaceType));
        Services.AddSingleton(vault, sp => sp.GetRequiredService(vaultInterfaceType));

        return this;
    }


    public abstract Task Build();
}

public interface IKeyspaceSetup
{
    IDatabaseServiceToken Token { get; }
    Task Build();
}

public class VaultRepositoryFactory
{
    private readonly IServiceProvider _provider;

    public VaultRepositoryFactory(IServiceProvider provider)
    {
        _provider = provider;
    }

    public IVaultRepository<TKeyspace> Make<TKeyspace>() where TKeyspace : class, IKeyspace, new()
    {
        var token = new TKeyspace().DatabaseToken;
        return _provider.GetServices<IVaultRepository<TKeyspace>>().Where(p => p.Token == token).First();
    }
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