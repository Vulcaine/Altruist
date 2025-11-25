/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD
{
    public interface IGameWorldManager3D : IGameWorldManager
    {
        IWorldIndex3D Index { get; }
        IPhysxWorld3D PhysxWorld { get; }

        Task<IEnumerable<WorldPartitionManager3D>> UpdateObjectPosition(IWorldObject3D obj);
        Task AddDynamicObject(IWorldObject3D obj);
        WorldPartitionManager3D? AddStaticObject(IWorldObject3D obj);
        IWorldObject3D? DestroyObject(string instanceId);
        IWorldObject3D? DestroyObject(IWorldObject3D obj);

        IEnumerable<IWorldObject3D> GetNearbyObjectsInRoom(
            string archetype,
            int x, int y, int z,
            float radius,
            string roomId);

        IEnumerable<WorldPartitionManager3D> FindPartitionsForPosition(int x, int y, int z, float radius);
        WorldPartitionManager3D? FindPartitionForPosition(int x, int y, int z);
    }

    public sealed class GameWorldManager3D : IGameWorldManager3D
    {
        private readonly IWorldIndex3D _index;
        private readonly IWorldPartitioner3D _worldPartitioner;
        private readonly Dictionary<PartitionIndex3D, WorldPartitionManager3D> _partitionMap = new();

        private readonly List<WorldPartitionManager3D> _partitions;
        private readonly IPhysxWorld3D _physx3D;

        public GameWorldManager3D(
            IWorldIndex3D world,
            IPhysxWorld3D physx3D,
            IWorldPartitioner3D worldPartitioner
        )
        {
            _index = world;
            _worldPartitioner = worldPartitioner ?? throw new ArgumentNullException(nameof(worldPartitioner));

            _physx3D = physx3D ?? throw new ArgumentNullException(nameof(physx3D));
            _partitions = new List<WorldPartitionManager3D>();
            Initialize();
        }

        public IPhysxWorld3D PhysxWorld => _physx3D;
        public IWorldIndex3D Index => _index;

        public void Initialize()
        {
            var partitions = _worldPartitioner.CalculatePartitions(_index);
            foreach (var partition in partitions)
            {
                _partitions.Add(partition);
                _partitionMap[new PartitionIndex3D(partition.Index.X, partition.Index.Y, partition.Index.Z)] = partition;
            }
        }

        public async Task<IEnumerable<WorldPartitionManager3D>> UpdateObjectPosition(IWorldObject3D obj)
        {
            if (obj is null)
                return Enumerable.Empty<WorldPartitionManager3D>();

            DestroyObject(obj);

            var radius = ComputePartitionRadius(obj);
            var partitions = FindPartitionsForPosition(
                obj.Transform.Position.X,
                obj.Transform.Position.Y,
                obj.Transform.Position.Z,
                radius);

            AddObjectToPartitions(obj, partitions);
            return await Task.FromResult(partitions.ToList());
        }

        public async Task AddDynamicObject(IWorldObject3D obj)
        {
            if (obj is null)
                return;

            var radius = ComputePartitionRadius(obj);
            var partitions = FindPartitionsForPosition(
                obj.Transform.Position.X,
                obj.Transform.Position.Y,
                obj.Transform.Position.Z,
                radius);

            AddObjectToPartitions(obj, partitions);
            await Task.CompletedTask;
        }

        public WorldPartitionManager3D? AddStaticObject(IWorldObject3D obj)
        {
            if (obj is null)
                return null;

            var partition = FindPartitionForPosition(
                obj.Transform.Position.X,
                obj.Transform.Position.Y,
                obj.Transform.Position.Z);

            partition?.AddObject(obj);
            return partition;
        }

        public IWorldObject3D? DestroyObject(string instanceId)
            => _partitions.Select(p => p.DestroyObject(instanceId)).FirstOrDefault(m => m != null);

        public IWorldObject3D? DestroyObject(IWorldObject3D obj)
            => DestroyObject(obj.InstanceId);

        public IEnumerable<IWorldObject3D> GetNearbyObjectsInRoom(
            string archetype,
            int x, int y, int z,
            float radius,
            string roomId)
        {
            var result = new List<IWorldObject3D>();
            var partitions = FindPartitionsForPosition(x, y, z, radius);
            foreach (var partition in partitions)
                result.AddRange(partition.GetObjectsByTypeInRadius(archetype, x, y, z, radius, roomId));

            return result.Distinct();
        }

        public WorldPartitionManager3D? FindPartitionForPosition(int x, int y, int z)
        {
            int indexX = (int)Math.Round(x / (double)_worldPartitioner.PartitionWidth);
            int indexY = (int)Math.Round(y / (double)_worldPartitioner.PartitionHeight);
            int indexZ = (int)Math.Round(z / (double)_worldPartitioner.PartitionDepth);

            return _partitionMap.TryGetValue(new PartitionIndex3D(indexX, indexY, indexZ), out var p) ? p : null;
        }

        public IEnumerable<WorldPartitionManager3D> FindPartitionsForPosition(int x, int y, int z, float radius)
        {
            float minX = x - radius;
            float maxX = x + radius;
            float minY = y - radius;
            float maxY = y + radius;
            float minZ = z - radius;
            float maxZ = z + radius;

            return _partitions.Where(partition =>
                maxX >= partition.Position.X &&
                minX <= partition.Position.X + partition.Size.X &&
                maxY >= partition.Position.Y &&
                minY <= partition.Position.Y + partition.Size.Y &&
                maxZ >= partition.Position.Z &&
                minZ <= partition.Position.Z + partition.Size.Z
            );
        }

        private IEnumerable<WorldPartitionManager3D> AddObjectToPartitions(
            IWorldObject3D obj,
            IEnumerable<WorldPartitionManager3D> partitions
        )
        {
            foreach (var partition in partitions)
                partition.AddObject(obj);
            return partitions;
        }

        /// <summary>
        /// Compute a partition search radius from the object's transform size.
        /// Uses half of the largest dimension; minimal floor if degenerate.
        /// </summary>
        private static float ComputePartitionRadius(IWorldObject3D obj)
        {
            var sz = obj.Transform.Size;
            var r = MathF.Max(sz.X, MathF.Max(sz.Y, sz.Z)) * 0.5f;

            if (r <= 0f || float.IsNaN(r) || float.IsInfinity(r))
                r = 0.5f; // minimal sensible radius
            return r;
        }
    }
}
