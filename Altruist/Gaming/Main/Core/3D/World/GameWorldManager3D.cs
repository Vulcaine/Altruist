/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD
{
    public interface IGameWorldManager3D : IGameWorldManager
    {
        Task<IEnumerable<WorldPartitionManager3D>> UpdateObjectPosition(IPrefab3D prefab);
        Task AddDynamicObject(IPrefab3D prefab);
        WorldPartitionManager3D? AddStaticObject(IPrefab3D prefab);
        IPrefab3D? DestroyObject(string instanceId);
        IPrefab3D? DestroyObject(IPrefab3D prefab);

        // Queries still accept a radius explicitly
        IEnumerable<IPrefab3D> GetNearbyObjectsInRoom(string prefabId, int x, int y, int z, float radius, string roomId);
        IEnumerable<WorldPartitionManager3D> FindPartitionsForPosition(int x, int y, int z, float radius);
        WorldPartitionManager3D? FindPartitionForPosition(int x, int y, int z);
    }

    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "3D")]
    [Service(typeof(IGameWorldManager))]
    [Service(typeof(IGameWorldManager3D))]
    public sealed class GameWorldManager3D : IGameWorldManager3D
    {
        private readonly WorldIndex3D _index;
        private readonly IWorldPartitioner3D _worldPartitioner;
        private readonly ICacheProvider _cache;
        private readonly IPrefabManager3D _prefabManager;
        private readonly Dictionary<PartitionIndex3D, WorldPartitionManager3D> _partitionMap = new();

        private readonly List<WorldPartitionManager3D> _partitions;
        private readonly IPhysxWorld3D _physx3D;

        public GameWorldManager3D(
            WorldIndex3D world,
            IPhysxWorld3D physx3D,
            IWorldPartitioner3D worldPartitioner,
            ICacheProvider cacheProvider,
            IPrefabManager3D prefabManager
        )
        {
            _index = world ?? throw new ArgumentNullException(nameof(world));
            _worldPartitioner = worldPartitioner ?? throw new ArgumentNullException(nameof(worldPartitioner));
            _cache = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));
            _prefabManager = prefabManager ?? throw new ArgumentNullException(nameof(prefabManager));

            _physx3D = physx3D ?? throw new ArgumentNullException(nameof(physx3D));
            _partitions = new List<WorldPartitionManager3D>();
        }

        public IPhysxWorld PhysxWorld => _physx3D;

        public void Initialize()
        {
            var partitions = _worldPartitioner.CalculatePartitions(_index);
            foreach (var partition in partitions)
            {
                _partitions.Add(partition);
                _partitionMap[new PartitionIndex3D(partition.Index.X, partition.Index.Y, partition.Index.Z)] = partition;
            }
        }

        public async Task<IEnumerable<WorldPartitionManager3D>> UpdateObjectPosition(IPrefab3D prefab)
        {
            if (prefab is null) return Enumerable.Empty<WorldPartitionManager3D>();

            DestroyObject(prefab);

            var radius = await ComputePartitionRadiusAsync(prefab);
            var partitions = FindPartitionsForPosition(
                prefab.Transform.Position.X,
                prefab.Transform.Position.Y,
                prefab.Transform.Position.Z,
                radius);

            AddObjectToPartitions(prefab, partitions);
            return partitions.ToList();
        }

        public async Task AddDynamicObject(IPrefab3D prefab)
        {
            if (prefab is null) return;

            var radius = await ComputePartitionRadiusAsync(prefab);
            var partitions = FindPartitionsForPosition(
                prefab.Transform.Position.X,
                prefab.Transform.Position.Y,
                prefab.Transform.Position.Z,
                radius);

            AddObjectToPartitions(prefab, partitions);
        }

        public WorldPartitionManager3D? AddStaticObject(IPrefab3D prefab)
        {
            if (prefab is null) return null;

            var partition = FindPartitionForPosition(
                prefab.Transform.Position.X,
                prefab.Transform.Position.Y,
                prefab.Transform.Position.Z);

            partition?.AddObject(prefab);
            return partition;
        }

        public IPrefab3D? DestroyObject(string instanceId)
            => _partitions.Select(p => p.DestroyObject(instanceId)).FirstOrDefault(m => m != null);

        public IPrefab3D? DestroyObject(IPrefab3D prefab)
            => DestroyObject(prefab.InstanceId);

        public IEnumerable<IPrefab3D> GetNearbyObjectsInRoom(string prefabId, int x, int y, int z, float radius, string roomId)
        {
            var result = new List<IPrefab3D>();
            var partitions = FindPartitionsForPosition(x, y, z, radius);
            foreach (var partition in partitions)
                result.AddRange(partition.GetObjectsByTypeInRadius(prefabId, x, y, z, radius, roomId));

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
            IPrefab3D prefab,
            IEnumerable<WorldPartitionManager3D> partitions
        )
        {
            foreach (var partition in partitions)
                partition.AddObject(prefab);
            return partitions;
        }

        /// <summary>
        /// Compute a partition search radius from collider AABB via PrefabManager3D.
        /// Uses half of the largest AABB dimension; minimal floor if degenerate.
        /// </summary>
        private async Task<float> ComputePartitionRadiusAsync(IPrefab3D prefab)
        {
            var bounds = await _prefabManager.ComputeBoundsAsync(prefab);
            var size = bounds.Size;
            var r = MathF.Max(size.X, MathF.Max(size.Y, size.Z)) * 0.5f;

            if (r <= 0f || float.IsNaN(r) || float.IsInfinity(r))
                r = 0.5f; // minimal sensible radius
            return r;
        }
    }
}
