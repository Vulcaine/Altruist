using Altruist.Numerics;

namespace Altruist.Gaming.TwoD
{
    /// <summary>
    /// Typed partition manager interface for 2D worlds, mirroring IWorldPartitionManager3D.
    /// <para>A partition is one rectangular cell of a world's coarse grid (sized by
    /// <see cref="IWorldPartitioner2D"/>) holding the objects that overlap it. Game code normally queries
    /// through <see cref="IGameWorldManager2D"/> (<c>GetNearbyObjectsInRoom</c>, <c>FindPartitionsForPosition</c>)
    /// rather than mutating partitions directly: adding or removing here does not touch the world's object
    /// index or physics.</para>
    /// </summary>
    public interface IWorldPartitionManager2D : IWorldPartitionManager
    {
        /// <summary>Files <paramref name="obj"/> in this partition's spatial grid at its current position.</summary>
        void AddObject(IWorldObject2D obj);
        /// <summary>Removes the object with this instance id from the partition.</summary>
        /// <returns>The removed object, or null when it was not in this partition.</returns>
        IWorldObject2D? DestroyObject(string instanceId);
        /// <summary>All objects in the partition with this archetype (a new set).</summary>
        HashSet<IWorldObject2D> GetObjectsByArchetype(string archetype);
        /// <summary>All objects in the partition assignable to <typeparamref name="T"/>.</summary>
        IEnumerable<T> GetAllObjects<T>() where T : IWorldObject2D;
        /// <summary>Objects with this archetype whose <c>ZoneId</c> equals <paramref name="roomId"/>.</summary>
        HashSet<IWorldObject2D> GetObjectsByTypeInRoom(string archetype, string roomId);
        /// <summary>Objects with this archetype and <c>ZoneId</c> == <paramref name="roomId"/> within
        /// <paramref name="radius"/> world units of (<paramref name="x"/>, <paramref name="y"/>).</summary>
        /// <param name="archetype">Archetype to match.</param>
        /// <param name="x">Center X, world units.</param>
        /// <param name="y">Center Y, world units.</param>
        /// <param name="radius">Search radius, world units.</param>
        /// <param name="roomId">Zone / room id the objects must have.</param>
        IEnumerable<IWorldObject2D> GetObjectsByTypeInRadius(string archetype, int x, int y, float radius, string roomId);
    }

    /// <summary>Default <see cref="IWorldPartitionManager2D"/>: one grid cell of a world, backed by a
    /// <see cref="SpatialGridIndex2D"/> with 16-unit cells. Created by <see cref="WorldPartitioner2D"/>;
    /// persisted to the cache by <see cref="IGameWorldManager2D.SaveAsync"/>. Not thread-safe.</summary>
    public class WorldPartition2D : StoredModel, IWorldPartitionManager2D
    {
        private readonly SpatialGridIndex2D _spatialIndex = new(cellSize: 16);
        /// <summary>Storage key (the <c>id</c> given at construction).</summary>
        public override string StorageId { get; set; } = Guid.NewGuid().ToString();

        /// <summary>Column / row of the partition in the world's grid.</summary>
        public IntVector2 Index { get; set; }
        /// <summary>Lower-left corner in world units.</summary>
        public IntVector2 Position { get; set; }
        /// <summary>Width / height in world units (edge partitions may be smaller than the configured size).</summary>
        public IntVector2 Size { get; set; }
        /// <summary>Center point: <c>Position + Size / 2</c> (integer division).</summary>
        public IntVector2 Epicenter { get; set; }
        /// <summary>Discriminator: <c>WorldPartition</c>.</summary>
        public override string Type { get; set; } = "WorldPartition";

        /// <summary>Creates an empty partition.</summary>
        /// <param name="id">Storage key.</param>
        /// <param name="index">Column / row in the grid.</param>
        /// <param name="position">Lower-left corner, world units.</param>
        /// <param name="size">Width / height, world units.</param>
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

        /// <inheritdoc/>
        public virtual void AddObject(IWorldObject2D prefab)
        {
            _spatialIndex.Add(prefab);
        }

        /// <inheritdoc/>
        public virtual IWorldObject2D? DestroyObject(string id)
        {
            return _spatialIndex.Remove(id);
        }

        /// <inheritdoc/>
        public virtual IEnumerable<IWorldObject2D> GetObjectsByTypeInRadius(string prefabId, int x, int y, float radius, string roomId)
        {
            return _spatialIndex.Query(prefabId, x, y, radius, roomId);
        }

        /// <summary>Same as <see cref="GetObjectsByArchetype"/>.</summary>
        public virtual HashSet<IWorldObject2D> GetObjectsByType(string prefabId) =>
            _spatialIndex.GetAllByType(prefabId);

        /// <inheritdoc/>
        public virtual HashSet<IWorldObject2D> GetObjectsByArchetype(string archetype) =>
            _spatialIndex.GetAllByType(archetype);

        /// <inheritdoc/>
        public virtual HashSet<IWorldObject2D> GetObjectsByTypeInRoom(string prefabId, string roomId) =>
            _spatialIndex.GetAllByType(prefabId).Where(x => x.ZoneId == roomId).ToHashSet();

        /// <inheritdoc/>
        public virtual IEnumerable<T> GetAllObjects<T>() where T : IWorldObject2D =>
            _spatialIndex.InstanceMap.Values.OfType<T>();
    }

    /// <summary>Splits a 2D world into a grid of <see cref="WorldPartition2D"/> cells. Replace the default
    /// <see cref="WorldPartitioner2D"/> by registering your own <c>[Service(typeof(IWorldPartitioner2D))]</c>.</summary>
    public interface IWorldPartitioner2D : IWorldPartitioner
    {
        /// <summary>Computes the partitions covering <paramref name="world"/>.</summary>
        /// <returns>New, empty partitions.</returns>
        List<WorldPartition2D> CalculatePartitions(IWorldIndex2D world);
    }

    /// <summary>Default <see cref="IWorldPartitioner2D"/> (singleton; registered when
    /// <c>altruist:environment:mode</c> is <c>2D</c>). Tiles the rectangle (0, 0)..<c>world.Size</c> with
    /// <see cref="PartitionWidth"/> x <see cref="PartitionHeight"/> cells, row by row; the last column / row is
    /// clipped to the world size. <c>world.Position</c> is not applied (partitions always start at the origin).
    /// Config: <c>altruist:game:worlds:partitioner:width</c> / <c>:height</c> (default 64).</summary>
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    [Service(typeof(IWorldPartitioner))]
    [Service(typeof(IWorldPartitioner2D))]
    public class WorldPartitioner2D : IWorldPartitioner2D
    {
        /// <inheritdoc/>
        public int PartitionWidth { get; }
        /// <inheritdoc/>
        public int PartitionHeight { get; }

        /// <summary>DI constructor.</summary>
        /// <param name="partitionWidth">Config <c>altruist:game:worlds:partitioner:width</c> (default 64).</param>
        /// <param name="partitionHeight">Config <c>altruist:game:worlds:partitioner:height</c> (default 64).</param>
        public WorldPartitioner2D(
            [AppConfigValue("altruist:game:worlds:partitioner:width", "64")]
            int partitionWidth,
            [AppConfigValue("altruist:game:worlds:partitioner:height", "64")]
            int partitionHeight
        )
        {
            PartitionWidth = partitionWidth;
            PartitionHeight = partitionHeight;
        }

        /// <inheritdoc/>
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
