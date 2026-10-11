/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming.ThreeD
{
    /// <summary>
    /// One axis-aligned cell of a 3D world's partition grid, holding the objects whose bounds intersect it in a spatial grid
    /// for archetype/room/radius queries. Callers normally go through <see cref="IGameWorldManager3D"/> (which keeps partitions
    /// in sync on spawn/destroy) rather than mutating partitions directly.
    /// </summary>
    public interface IWorldPartitionManager3D : IWorldPartitionManager
    {
        /// <summary>Adds (or re-adds) an object, filed by its current <c>Transform.Position</c>.</summary>
        /// <param name="obj">The object; <c>null</c> is ignored.</param>
        void AddObject(IWorldObject3D obj);
        /// <summary>Removes an object from this partition only (no physics or world-cache side effects).</summary>
        /// <param name="instanceId">Instance id.</param>
        /// <returns>The removed object, or <c>null</c> if it was not in this partition.</returns>
        IWorldObject3D? DestroyObject(string instanceId);

        /// <summary>Return all objects with the given archetype.</summary>
        IEnumerable<IWorldObject3D> GetObjectsByArchetype(string archetype);

        /// <summary>Returns a new set with every object in this partition assignable to <typeparamref name="T"/>.</summary>
        /// <typeparam name="T">World object type to filter by.</typeparam>
        HashSet<T> GetAllObjects<T>() where T : IWorldObject3D;

        /// <summary>Return all objects with the given archetype within a given room.</summary>
        IEnumerable<IWorldObject3D> GetObjectsByTypeInRoom(string archetype, string roomId);

        /// <summary>
        /// Query by archetype within a radius in a room. Coordinates are world-space.
        /// </summary>
        IEnumerable<IWorldObject3D> GetObjectsByTypeInRadius(
            string archetype,
            int x, int y, int z,
            float radius,
            string roomId);
    }

    /// <summary>
    /// Manages a spatial partition of the 3D world using a grid index.
    /// </summary>
    /// <remarks>
    /// Created by <see cref="IWorldPartitioner3D.CalculatePartitions"/>. Uses a <see cref="SpatialGridIndex3D"/> with 16-unit cells.
    /// Not thread-safe. Members are virtual so a custom partitioner can return a subclass.
    /// </remarks>
    public class WorldPartitionManager3D : IWorldPartitionManager3D
    {
        private readonly SpatialGridIndex3D _spatialIndex = new(cellSize: 16);

        /// <summary>Grid coordinates (column, row, slice) of this partition.</summary>
        public IntVector3 Index { get; set; }
        /// <summary>Minimum corner in world units.</summary>
        public IntVector3 Position { get; set; }
        /// <summary>Extent in world units (edge partitions may be smaller than the configured partition size).</summary>
        public IntVector3 Size { get; set; }
        /// <summary>Center (<c>Position + Size / 2</c>, integer division), computed at construction.</summary>
        public IntVector3 Epicenter { get; set; }

        /// <summary>Creates an empty partition.</summary>
        /// <param name="index">Grid coordinates.</param>
        /// <param name="position">Minimum corner in world units.</param>
        /// <param name="size">Extent in world units.</param>
        public WorldPartitionManager3D(
            IntVector3 index, IntVector3 position, IntVector3 size)
        {
            Index = index;
            Position = position;
            Size = size;
            Epicenter = position + size / 2;
        }

        /// <inheritdoc/>
        public virtual void AddObject(IWorldObject3D obj)
        {
            if (obj is null)
                return;
            _spatialIndex.Add(obj);
        }

        /// <inheritdoc/>
        public virtual IWorldObject3D? DestroyObject(string instanceId)
        {
            return _spatialIndex.Remove(instanceId);
        }

        /// <inheritdoc/>
        public virtual IEnumerable<IWorldObject3D> GetObjectsByTypeInRadius(
            string archetype,
            int x, int y, int z,
            float radius,
            string roomId)
        {
            return _spatialIndex.Query(archetype, x, y, z, radius, roomId);
        }

        /// <inheritdoc/>
        public virtual IEnumerable<IWorldObject3D> GetObjectsByArchetype(string archetype) =>
            _spatialIndex.GetAllByType(archetype);

        /// <inheritdoc/>
        public virtual IEnumerable<IWorldObject3D> GetObjectsByTypeInRoom(string archetype, string roomId)
        {
            foreach (var obj in _spatialIndex.GetAllByType(archetype))
            {
                if (string.Equals(obj.ZoneId, roomId, StringComparison.Ordinal))
                    yield return obj;
            }
        }


        /// <inheritdoc/>
        public override string ToString()
        {
            var objectCount = _spatialIndex.InstanceMap.Count;
            var min = Position;
            var max = Position + Size;
            return $"Partition {Index} [{min}..{max}] epicenter={Epicenter} objects={objectCount}";
        }

        /// <inheritdoc/>
        public HashSet<T> GetAllObjects<T>() where T : IWorldObject3D
        {
            return _spatialIndex.InstanceMap.Values
                .OfType<T>()
                .ToHashSet();
        }
    }

    /// <summary>
    /// Splits a 3D world into a regular grid of <see cref="WorldPartitionManager3D"/> cells. Replace the registration to
    /// customize partitioning; for 2D see <see cref="Altruist.Gaming.TwoD.IWorldPartitioner2D"/>.
    /// </summary>
    public interface IWorldPartitioner3D : IWorldPartitioner
    {
        /// <summary>Partition extent along Z in world units.</summary>
        int PartitionDepth { get; }
        /// <summary>Builds the partitions covering <c>[0, world.Size)</c> on each axis (world <c>Position</c> is not applied).</summary>
        /// <param name="world">World whose <c>Size</c> is partitioned.</param>
        /// <returns>A new list of partitions, ordered by slice, then row, then column.</returns>
        List<WorldPartitionManager3D> CalculatePartitions(IWorldIndex3D world);
    }

    /// <summary>
    /// Default <see cref="IWorldPartitioner3D"/> (3D mode): fixed-size cells read from
    /// <c>altruist:game:worlds:partitioner:width|height|depth</c> (default 64 each); the last cell on each axis is clipped to the world size.
    /// </summary>
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    [Service(typeof(IWorldPartitioner))]
    [Service(typeof(IWorldPartitioner3D))]
    public class WorldPartitioner3D : IWorldPartitioner3D
    {
        /// <summary>Partition extent along X in world units.</summary>
        public int PartitionWidth { get; }
        /// <summary>Partition extent along Y in world units.</summary>
        public int PartitionHeight { get; }
        /// <inheritdoc/>
        public int PartitionDepth { get; }

        /// <summary>Creates the partitioner.</summary>
        /// <param name="partitionWidth">Cell size along X (<c>altruist:game:worlds:partitioner:width</c>).</param>
        /// <param name="partitionHeight">Cell size along Y (<c>altruist:game:worlds:partitioner:height</c>).</param>
        /// <param name="partitionDepth">Cell size along Z (<c>altruist:game:worlds:partitioner:depth</c>).</param>
        public WorldPartitioner3D(
            [AppConfigValue("altruist:game:worlds:partitioner:width", "64")]
            int partitionWidth,
            [AppConfigValue("altruist:game:worlds:partitioner:height", "64")]
            int partitionHeight,
            [AppConfigValue("altruist:game:worlds:partitioner:depth", "64")]
            int partitionDepth)
        {
            PartitionWidth = partitionWidth;
            PartitionHeight = partitionHeight;
            PartitionDepth = partitionDepth;
        }

        /// <inheritdoc/>
        public List<WorldPartitionManager3D> CalculatePartitions(IWorldIndex3D world)
        {
            var partitions = new List<WorldPartitionManager3D>();

            int columns = (int)Math.Ceiling((double)world.Size.X / PartitionWidth);
            int rows = (int)Math.Ceiling((double)world.Size.Y / PartitionHeight);
            int slices = (int)Math.Ceiling((double)world.Size.Z / PartitionDepth);

            for (int slice = 0; slice < slices; slice++)
            {
                for (int row = 0; row < rows; row++)
                {
                    for (int col = 0; col < columns; col++)
                    {
                        int x = col * PartitionWidth;
                        int y = row * PartitionHeight;
                        int z = slice * PartitionDepth;

                        int width = Math.Min(PartitionWidth, world.Size.X - x);
                        int height = Math.Min(PartitionHeight, world.Size.Y - y);
                        int depth = Math.Min(PartitionDepth, world.Size.Z - z);

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
