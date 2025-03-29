using Altruist.Database;

namespace Altruist.ScyllaDB;

public static class Extension
{
    public static AltruistApplicationBuilder WithScyllaDB(this AltruistDatabaseBuilder builder, Func<ScyllaDBConnectionSetup, ScyllaDBConnectionSetup>? setup = null)
    {
        return builder.SetupDatabase(ScyllaDBToken.Instance, setup);
    }

    public static KeyspaceSetup<TKeyspace> ForgeVaults<TKeyspace>(this KeyspaceSetup<TKeyspace> setup)
     where TKeyspace : class, IKeyspace
    {
        var vaultModelTypes = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetTypes())
            .Where(t => typeof(IVaultModel).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false });

        foreach (var type in vaultModelTypes)
        {
            setup.AddVault(type);
        }

        return setup;
    }

}