
using Altruist.Contracts;
using Altruist.Database;
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

    public ScyllaDBConnectionSetup(IServiceCollection services, IDatabaseServiceToken token) : base(services, token)
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


    public override void Build(IAltruistContext settings)
    {
        ILoggerFactory factory = _services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger<ScyllaDBConnectionSetup>();

        if (_contactPoints.Count == 0)
        {
            _contactPoints.Add("localhost:9042");
        }

        _services.AddSingleton<IScyllaDbProvider>(sp => new ScyllaDbProvider(_contactPoints, Token));

        _services.AddSingleton(sp => new ScyllaVaultFactory(sp.GetRequiredService<IScyllaDbProvider>()));
        _services.AddSingleton<IDatabaseVaultFactory>(sp => sp.GetRequiredService<ScyllaVaultFactory>());

        if (Keyspaces.Count == 0)
        {
            new ScyllaKeyspaceSetup<DefaultScyllaKeyspace>(_services, new DefaultScyllaKeyspace(), Token).Build();
        }
        else
        {
            foreach (var keyspaceSetup in Keyspaces.Values)
            {
                keyspaceSetup.Build();
            }
        }

        logger.LogInformation("⚡ ScyllaDB support activated. Ready to store and distribute data across realms with incredible speed! 🌌");
    }
}

public class ScyllaKeyspaceSetup<TKeyspace> : KeyspaceSetup<TKeyspace> where TKeyspace : class, IScyllaKeyspace, new()
{
    public ScyllaKeyspaceSetup(IServiceCollection services, TKeyspace instance, IDatabaseServiceToken token) : base(services, instance, token)
    {
        services.AddSingleton<IVaultRepository<TKeyspace>>(sp =>
        {
            var provider = sp.GetRequiredService<IScyllaDbProvider>();
            return new ScyllaVaultRepository<TKeyspace>(sp, provider, instance);
        });

        services.AddSingleton(typeof(ScyllaVaultRepository<TKeyspace>), sp => sp.GetRequiredService<IVaultRepository<TKeyspace>>());
    }

    public override void Build()
    {
        var provider = Services
                .BuildServiceProvider()
                .GetService<IScyllaDbProvider>();

        if (provider == null)
        {
            throw new InvalidOperationException("ScyllaDB provider is not registered.");
        }

        provider.CreateKeySpaceAsync(Instance.Name, Instance.Options);
        foreach (var vault in VaultModels)
        {
            provider.CreateTableAsync(vault, Instance);
        }
    }
}