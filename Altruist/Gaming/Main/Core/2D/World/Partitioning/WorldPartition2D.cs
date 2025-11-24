using Altruist.Numerics;

namespace Altruist.Gaming.TwoD
{
    public class WorldPartition2D : StoredModel, IWorldPartitionManager
    {
        private readonly SpatialGridIndex2D _spatialIndex = new(cellSize: 16);
        public override string StorageId { get; set; } = Guid.NewGuid().ToString();

        public IntVector2 Index { get; set; }
        public IntVector2 Position { get; set; }
        public IntVector2 Size { get; set; }
        public IntVector2 Epicenter { get; set; }
        public override string Type { get; set; } = "WorldPartition";

        public WorldPartition2D(
            string id,
            IntVector2 index, IntVector2 position, IntVector2 size)
        {
            StorageId = id;
            Index = index;
            Position = position;
            Size = size;
            Epicenter = position + size / 2;
        }

        public virtual void AddObject(IWorldObject2D prefab)
        {
            _spatialIndex.Add(prefab);
        }

        public virtual IWorldObject2D? DestroyObject(string id)
        {
            return _spatialIndex.Remove(id);
        }

        public virtual IEnumerable<IWorldObject2D> GetObjectsByTypeInRadius(string prefabId, int x, int y, float radius, string roomId)
        {
            return _spatialIndex.Query(prefabId, x, y, radius, roomId);
        }

        public virtual HashSet<IWorldObject2D> GetObjectsByType(string prefabId) =>
            _spatialIndex.GetAllByType(prefabId);

        public virtual HashSet<IWorldObject2D> GetObjectsByTypeInRoom(string prefabId, string roomId) =>
            _spatialIndex.GetAllByType(prefabId).Where(x => x.RoomId == roomId).ToHashSet();
    }

    public interface IWorldPartitioner2D : IWorldPartitioner
    {
        List<WorldPartition2D> CalculatePartitions(IWorldIndex2D world);
    }

    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    [Service(typeof(IWorldPartitioner))]
    [Service(typeof(IWorldPartitioner2D))]
    public class WorldPartitioner2D : IWorldPartitioner2D
    {
        public int PartitionWidth { get; }
        public int PartitionHeight { get; }

        public WorldPartitioner2D(
            [AppConfigValue("altruist:game:partitioner:width", "64")]
            int partitionWidth,
            [AppConfigValue("altruist:game:partitioner:height", "64")]
            int partitionHeight
        )
        {
            PartitionWidth = partitionWidth;
            PartitionHeight = partitionHeight;
        }

        public List<WorldPartition2D> CalculatePartitions(IWorldIndex2D world)
        {
            var partitions = new List<WorldPartition2D>();

            int columns = (int)Math.Ceiling((double)world.Size.X / PartitionWidth);
            int rows = (int)Math.Ceiling((double)world.Size.Y / PartitionHeight);

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < columns; col++)
                {
                    float x = col * PartitionWidth;
                    float y = row * PartitionHeight;

                    float width = Math.Min(PartitionWidth, world.Size.X - x);
                    float height = Math.Min(PartitionHeight, world.Size.Y - y);

                    var partition = new WorldPartition2D(
                        id: Guid.NewGuid().ToString(),
                        index: new IntVector2(col, row),
                        position: new IntVector2((int)x, (int)y),
                        size: new IntVector2((int)width, (int)height)
                    );

                    partitions.Add(partition);
                }
            }

            return partitions;
        }
    }
}
