using Altruist.Gaming.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD
{
    public class WorldPartition3D : StoredModel, IWorldPartition
    {
        private readonly SpatialGridIndex3D _spatialIndex = new(cellSize: 16);
        public override string SysId { get; set; } = Guid.NewGuid().ToString();

        public IntVector3 Index { get; set; }
        public IntVector3 Position { get; set; }
        public IntVector3 Size { get; set; }
        public IntVector3 Epicenter { get; set; }
        public override string Type { get; set; } = "WorldPartition3D";

        public WorldPartition3D(
            string id,
            IntVector3 index, IntVector3 position, IntVector3 size)
        {
            SysId = id;
            Index = index;
            Position = position;
            Size = size;
            Epicenter = position + size / 2;
        }

        public virtual void AddObject(WorldObjectTypeKey objectType, IObjectMetadata objectMetadata)
        {
            _spatialIndex.Add(objectType, objectMetadata);
        }

        public virtual IObjectMetadata? DestroyObject(WorldObjectTypeKey objectType, string id)
        {
            return _spatialIndex.Remove(objectType, id);
        }

        public virtual IEnumerable<IObjectMetadata> GetObjectsByTypeInRadius(
            WorldObjectTypeKey objectType,
            int x, int y, int z,
            float radius,
            string roomId)
        {
            return _spatialIndex.Query(objectType, x, y, z, radius, roomId);
        }

        public virtual HashSet<IObjectMetadata> GetObjectsByType(WorldObjectTypeKey objectType) =>
            _spatialIndex.GetAllByType(objectType);

        public virtual HashSet<IObjectMetadata> GetObjectsByTypeInRoom(WorldObjectTypeKey objectType, string roomId) =>
            _spatialIndex.GetAllByType(objectType).Where(x => x.RoomId == roomId).ToHashSet();
    }

    public interface IWorldPartitioner3D : IWorldPartitioner
    {
        int PartitionDepth { get; }
        List<WorldPartition3D> CalculatePartitions(WorldIndex3D world);
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
            [ConfigValue("altruist:game:partitioner:width", "64")]
            int partitionWidth,
            [ConfigValue("altruist:game:partitioner:height", "64")]
            int partitionHeight,
            [ConfigValue("altruist:game:partitioner:depth", "64")]
            int partitionDepth)
        {
            PartitionWidth = partitionWidth;
            PartitionHeight = partitionHeight;
            PartitionDepth = partitionDepth;
        }

        public List<WorldPartition3D> CalculatePartitions(WorldIndex3D world)
        {
            var partitions = new List<WorldPartition3D>();

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

                        var partition = new WorldPartition3D(
                            id: Guid.NewGuid().ToString(),
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
