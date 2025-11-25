/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Numerics;

namespace Altruist.Gaming.ThreeD
{
    public interface IWorldPartitionManager3D : IWorldPartitionManager
    {
        void AddObject(IWorldObject3D obj);
        IWorldObject3D? DestroyObject(string instanceId);

        /// <summary>Return all objects with the given archetype.</summary>
        HashSet<IWorldObject3D> GetObjectsByType(string archetype);

        /// <summary>Return all objects with the given archetype within a given room.</summary>
        HashSet<IWorldObject3D> GetObjectsByTypeInRoom(string archetype, string roomId);

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

        public virtual void AddObject(IWorldObject3D obj)
        {
            if (obj is null)
                return;
            _spatialIndex.Add(obj);
        }

        public virtual IWorldObject3D? DestroyObject(string instanceId)
        {
            return _spatialIndex.Remove(instanceId);
        }

        public virtual IEnumerable<IWorldObject3D> GetObjectsByTypeInRadius(
            string archetype,
            int x, int y, int z,
            float radius,
            string roomId)
        {
            return _spatialIndex.Query(archetype, x, y, z, radius, roomId);
        }

        public virtual HashSet<IWorldObject3D> GetObjectsByType(string archetype) =>
            _spatialIndex.GetAllByType(archetype);

        public virtual HashSet<IWorldObject3D> GetObjectsByTypeInRoom(string archetype, string roomId) =>
            _spatialIndex.GetAllByType(archetype)
                         .Where(x => string.Equals(x.ZoneId, roomId, StringComparison.Ordinal))
                         .ToHashSet();


        public override string ToString()
        {
            var objectCount = _spatialIndex.InstanceMap.Count;
            var min = Position;
            var max = Position + Size;
            return $"Partition {Index} [{min}..{max}] epicenter={Epicenter} objects={objectCount}";
        }
    }

    public interface IWorldPartitioner3D : IWorldPartitioner
    {
        int PartitionDepth { get; }
        List<WorldPartitionManager3D> CalculatePartitions(IWorldIndex3D world);
    }

    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    [Service(typeof(IWorldPartitioner))]
    [Service(typeof(IWorldPartitioner3D))]
    public class WorldPartitioner3D : IWorldPartitioner3D
    {
        public int PartitionWidth { get; }
        public int PartitionHeight { get; }
        public int PartitionDepth { get; }

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
