namespace Altruist.ScyllaDB;

public static class Extension
{
    public static AltruistApplicationBuilder WithScyllaDB(this AltruistDatabaseBuilder builder, Func<ScyllaDBConnectionSetup, ScyllaDBConnectionSetup>? setup = null)
    {
        return builder.SetupDatabase(ScyllaDBToken.Instance, setup);
    }
}