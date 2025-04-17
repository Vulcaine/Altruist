
using Altruist.Contracts;
using Altruist.Database;

namespace Altruist.ScyllaDB;

public enum ReplicationStrategy
{
    SimpleStrategy,
    NetworkTopologyStrategy
}



public class ScyllaReplicationOptions : ReplicationOptions
{
    public ReplicationStrategy Strategy { get; set; } = ReplicationStrategy.SimpleStrategy;
}



public abstract class ScyllaKeyspace : IScyllaKeyspace
{
    public string Name { get; set; } = "altruist";
    public ScyllaReplicationOptions? Options { get; set; } = new ScyllaReplicationOptions();

    public IDatabaseServiceToken DatabaseToken => ScyllaDBToken.Instance;
}

public class DefaultScyllaKeyspace : ScyllaKeyspace
{
}
