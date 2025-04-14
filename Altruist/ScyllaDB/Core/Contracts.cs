using Cassandra;

namespace Altruist.ScyllaDB;


public interface IScyllaKeyspace : IKeyspace
{
    ScyllaReplicationOptions? Options { get; set; }
}

public interface IScyllaDbProvider : ICqlDatabaseProvider
{
    Task ShutdownAsync(Exception? ex = null);
    event Action<Host> HostAdded;
    event Action<Host> HostRemoved;
}
