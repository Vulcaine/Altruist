using Altruist.Gaming.TwoD.Numerics;

namespace Altruist.Gaming.TwoD
{
    public class WorldPartition2D : StoredModel
    {
        private readonly SpatialGridIndex2D _spatialIndex = new(cellSize: 16);
        public override string SysId { get; set; } = Guid.NewGuid().ToString();

        public IntVector2 Index { get; set; }
        public IntVector2 Position { get; set; }
        public IntVector2 Size { get; set; }
        public IntVector2 Epicenter { get; set; }
        public override string Type { get; set; } = "WorldPartition";

        public WorldPartition2D(
            string id,
            IntVector2 index, IntVector2 position, IntVector2 size)
        {
            SysId = id;
            Index = index;
            Position = position;
            Size = size;
            Epicenter = position + size / 2;
        }

        public virtual void AddObject(WorldObjectTypeKey objectType, ObjectMetadata2D objectMetadata)
        {
            _spatialIndex.Add(objectType, objectMetadata);
        }

        public virtual ObjectMetadata2D? DestroyObject(WorldObjectTypeKey objectType, string id)
        {
            return _spatialIndex.Remove(objectType, id);
        }

        public virtual IEnumerable<ObjectMetadata2D> GetObjectsByTypeInRadius(WorldObjectTypeKey objectType, int x, int y, float radius, string roomId)
        {
            return _spatialIndex.Query(objectType, x, y, radius, roomId);
        }

        public virtual HashSet<ObjectMetadata2D> GetObjectsByType(WorldObjectTypeKey objectType) =>
            _spatialIndex.GetAllByType(objectType);

        public virtual HashSet<ObjectMetadata2D> GetObjectsByTypeInRoom(WorldObjectTypeKey objectType, string roomId) =>
            _spatialIndex.GetAllByType(objectType).Where(x => x.RoomId == roomId).ToHashSet();
    }

    public interface IWorldPartitioner2D
    {
        int PartitionWidth { get; }
        int PartitionHeight { get; }
        List<WorldPartition2D> CalculatePartitions(WorldIndex2D world);
    }

    public class WorldPartitioner2D : IWorldPartitioner2D
    {
        public int PartitionWidth { get; }
        public int PartitionHeight { get; }

        public WorldPartitioner2D(int partitionWidth, int partitionHeight)
        {
            PartitionWidth = partitionWidth;
            PartitionHeight = partitionHeight;
        }

        public List<WorldPartition2D> CalculatePartitions(WorldIndex2D world)
        {
            var partitions = new List<WorldPartition2D>();

            int columns = (int)Math.Ceiling((double)world.Width / PartitionWidth);
            int rows = (int)Math.Ceiling((double)world.Height / PartitionHeight);

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < columns; col++)
                {
                    int x = col * PartitionWidth;
                    int y = row * PartitionHeight;

                    int width = Math.Min(PartitionWidth, world.Width - x);
                    int height = Math.Min(PartitionHeight, world.Height - y);

                    var partition = new WorldPartition2D(
                        id: Guid.NewGuid().ToString(),
                        index: new IntVector2(col, row),
                        position: new IntVector2(x, y),
                        size: new IntVector2(width, height)
                    );

                    partitions.Add(partition);
                }
            }

            return partitions;
        }
    }
}