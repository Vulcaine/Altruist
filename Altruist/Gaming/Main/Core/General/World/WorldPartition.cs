namespace Altruist.Gaming
{
    /// <summary>Splits a world into equal rectangular partitions (base of <c>IWorldPartitioner2D</c> /
    /// <c>IWorldPartitioner3D</c>). Partition size bounds zone size.</summary>
    public interface IWorldPartitioner
    {
        /// <summary>Width of one partition in world units.</summary>
        int PartitionWidth { get; }
        /// <summary>Height (2D) or depth of one partition in world units.</summary>
        int PartitionHeight { get; }
    }
    /// <summary>Empty marker interface; no members and no implementations in the framework.</summary>
    public interface IWorldPartitionManager
    {

    }
}
