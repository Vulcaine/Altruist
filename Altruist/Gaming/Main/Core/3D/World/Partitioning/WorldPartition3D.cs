using Altruist.Numerics;

namespace Altruist.Gaming.ThreeD
{
    public interface IWorldPartitionManager3D : IWorldPartitionManager
    {
        void AddObject(IPrefab3D objectMetadata);
        IPrefab3D? DestroyObject(string instanceId);
        HashSet<IPrefab3D> GetObjectsByType(string prefabId);
        HashSet<IPrefab3D> GetObjectsByTypeInRoom(string prefabId, string roomId);
    }

    public class WorldPartitionManager3D : IWorldPartitionManager3D
    {
        private readonly SpatialGridIndex3D _spatialIndex = new(cellSize: 16);

        public IntVector3 Index { get; set; }
        public IntVector3 Position { get; set; }
        public IntVector3 Size { get; set; }
        public IntVector3 Epicenter { get; set; }

        public WorldPartitionManager3D(
            IntVector3 index, IntVector3 position, IntVector3 size)
        {
            Index = index;
            Position = position;
            Size = size;
            Epicenter = position + size / 2;
        }

        public virtual void AddObject(IPrefab3D prefab)
        {
            _spatialIndex.Add(prefab);
        }

        public virtual IPrefab3D? DestroyObject(string instanceId)
        {
            return _spatialIndex.Remove(instanceId);
        }

        public virtual IEnumerable<IPrefab3D> GetObjectsByTypeInRadius(
            string prefabId,
            int x, int y, int z,
            float radius,
            string roomId)
        {
            return _spatialIndex.Query(prefabId, x, y, z, radius, roomId);
        }

        public virtual HashSet<IPrefab3D> GetObjectsByType(string prefabId) =>
            _spatialIndex.GetAllByType(prefabId);

        public virtual HashSet<IPrefab3D> GetObjectsByTypeInRoom(string prefabId, string roomId) =>
            _spatialIndex.GetAllByType(prefabId).Where(x => x.RoomId == roomId).ToHashSet();
    }

    public interface IWorldPartitioner3D : IWorldPartitioner
    {
        int PartitionDepth { get; }
        List<WorldPartitionManager3D> CalculatePartitions(WorldIndex3D world);
    }

    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "3D")]
    [Service(typeof(IWorldPartitioner))]
    [Service(typeof(IWorldPartitioner3D))]
    public class WorldPartitioner3D : IWorldPartitioner3D
    {
        public int PartitionWidth { get; }
        public int PartitionHeight { get; }
        public int PartitionDepth { get; }

        public WorldPartitioner3D(
            [AppConfigValue("altruist:game:partitioner:width", "64")]
            int partitionWidth,
            [AppConfigValue("altruist:game:partitioner:height", "64")]
            int partitionHeight,
            [AppConfigValue("altruist:game:partitioner:depth", "64")]
            int partitionDepth)
        {
            PartitionWidth = partitionWidth;
            PartitionHeight = partitionHeight;
            PartitionDepth = partitionDepth;
        }

        public List<WorldPartitionManager3D> CalculatePartitions(WorldIndex3D world)
        {
            var partitions = new List<WorldPartitionManager3D>();

            int columns = (int)Math.Ceiling((double)world.Width / PartitionWidth);
            int rows = (int)Math.Ceiling((double)world.Height / PartitionHeight);
            int slices = (int)Math.Ceiling((double)world.Depth / PartitionDepth);

            for (int slice = 0; slice < slices; slice++)
            {
                for (int row = 0; row < rows; row++)
                {
                    for (int col = 0; col < columns; col++)
                    {
                        int x = col * PartitionWidth;
                        int y = row * PartitionHeight;
                        int z = slice * PartitionDepth;

                        int width = Math.Min(PartitionWidth, world.Width - x);
                        int height = Math.Min(PartitionHeight, world.Height - y);
                        int depth = Math.Min(PartitionDepth, world.Depth - z);

                        var partition = new WorldPartitionManager3D(
                            index: new IntVector3(col, row, slice),
                            position: new IntVector3(x, y, z),
                            size: new IntVector3(width, height, depth)
                        );

                        partitions.Add(partition);
                    }
                }
            }

            return partitions;
        }
    }
}
