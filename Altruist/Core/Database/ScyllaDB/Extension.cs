namespace Altruist.ScyllaDB;

public static class Extension
{
    public static AltruistServerBuilder WithScyllaDB(this AltruistDatabaseBuilder builder)
    {
        Console.WriteLine("WITH SCYLLA");
        return builder.SetupDatabase<ScyllaDBConnectionSetup>(ScyllaDBToken.Instance);
    }
}