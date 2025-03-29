namespace Altruist.ScyllaDB;

public static class Extension
{
    public static AltruistServerBuilder WithScyllaDB(this AltruistDatabaseBuilder builder, Func<ScyllaDBConnectionSetup, ScyllaDBConnectionSetup>? setup = null)
    {
        return builder.SetupDatabase<ScyllaDBConnectionSetup>(ScyllaDBToken.Instance, setup);
    }
}