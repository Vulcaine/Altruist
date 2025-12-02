/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;

namespace Altruist.Gaming.TwoD
{
    public interface IGameWorldManager2D : IGameWorldManager
    {
        IWorldIndex2D Index { get; }
        IPhysxWorld PhysxWorld { get; }
        void Initialize();
        Task SaveAsync();

        Task<IEnumerable<IWorldPartitionManager>> UpdateObjectPosition(IWorldObject2D obj);
        Task AddDynamicObject(IWorldObject2D obj);

        IWorldPartitionManager? AddStaticObject(IWorldObject2D obj);
        IWorldObject2D? DestroyObject(string instanceId);
        IWorldObject2D? DestroyObject(IWorldObject2D obj);

        IEnumerable<IWorldObject2D> GetNearbyObjectsInRoom(
            string archetype,
            int x, int y,
            float radius,
            string roomId);

        IEnumerable<IWorldPartitionManager> FindPartitionsForPosition(int x, int y, float radius);
        IWorldPartitionManager? FindPartitionForPosition(int x, int y);
    }

    public sealed class GameWorldManager2D : IGameWorldManager2D
    {
        private readonly IWorldIndex2D _index;
        private readonly IWorldPartitioner2D _worldPartitioner;
        private readonly ICacheProvider _cache;
        private readonly Dictionary<PartitionIndex2D, IWorldPartitionManager> _partitionMap = new();

        private readonly List<WorldPartition2D> _partitions;
        private readonly IPhysxWorld2D _physx2D;

        public GameWorldManager2D(
            IWorldIndex2D world,
            IPhysxWorld2D physx2D,
            IWorldPartitioner2D worldPartitioner,
            ICacheProvider cacheProvider
        )
        {
            _index = world ?? throw new ArgumentNullException(nameof(world));
            _worldPartitioner = worldPartitioner ?? throw new ArgumentNullException(nameof(worldPartitioner));
            _cache = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));

            _physx2D = physx2D ?? throw new ArgumentNullException(nameof(physx2D));
            _partitions = new List<WorldPartition2D>();
        }

        public IPhysxWorld PhysxWorld => _physx2D;
        public IWorldIndex2D Index => _index;

        public void Initialize()
        {
            var partitions = _worldPartitioner.CalculatePartitions(_index);
            foreach (var partition in partitions)
            {
                _partitions.Add(partition);
                _partitionMap[new PartitionIndex2D(partition.Index.X, partition.Index.Y)] = partition;
            }

            _ = SaveAsync();
        }

        public async Task SaveAsync()
        {
            var saveTasks = _partitions.Select(p => _cache.SaveAsync(p.StorageId, p));
            await Task.WhenAll(saveTasks);
        }

        public async Task<IEnumerable<IWorldPartitionManager>> UpdateObjectPosition(IWorldObject2D obj)
        {
            if (obj is null)
                return Enumerable.Empty<IWorldPartitionManager>();

            DestroyObject(obj);

            var radius = ComputePartitionRadius(obj);

            var partitions = FindPartitionsForPosition(
                obj.Transform.Position.X,
                obj.Transform.Position.Y,
                radius);

            AddObjectToPartitions(obj, partitions);
            return await Task.FromResult(partitions.ToList());
        }

        public async Task AddDynamicObject(IWorldObject2D obj)
        {
            if (obj is null)
                return;

            var radius = ComputePartitionRadius(obj);

            var partitions = FindPartitionsForPosition(
                obj.Transform.Position.X,
                obj.Transform.Position.Y,
                radius);

            AddObjectToPartitions(obj, partitions);
            await Task.CompletedTask;
        }

        public IWorldPartitionManager? AddStaticObject(IWorldObject2D obj)
        {
            if (obj is null)
                return null;

            var partition = FindPartitionForPosition(
                obj.Transform.Position.X,
                obj.Transform.Position.Y);

            if (partition is WorldPartition2D p2d)
            {
                p2d.AddObject(obj);
            }

            return partition;
        }

        public IWorldObject2D? DestroyObject(string instanceId)
            => _partitions.Select(p => p.DestroyObject(instanceId)).FirstOrDefault(m => m != null);

        public IWorldObject2D? DestroyObject(IWorldObject2D obj)
            => DestroyObject(obj.InstanceId);

        public IEnumerable<IWorldObject2D> GetNearbyObjectsInRoom(
            string archetype,
            int x, int y,
            float radius,
            string roomId)
        {
            var result = new List<IWorldObject2D>();
            var partitions = FindPartitionsForPosition(x, y, radius);

            foreach (var partition in partitions)
            {
                if (partition is WorldPartition2D p2d)
                {
                    result.AddRange(p2d.GetObjectsByTypeInRadius(archetype, x, y, radius, roomId));
                }
            }

            return result.Distinct();
        }

        public IWorldPartitionManager? FindPartitionForPosition(int x, int y)
        {
            int indexX = (int)Math.Round(x / (double)_worldPartitioner.PartitionWidth);
            int indexY = (int)Math.Round(y / (double)_worldPartitioner.PartitionHeight);

            return _partitionMap.TryGetValue(new PartitionIndex2D(indexX, indexY), out var p) ? p : null;
        }

        public IEnumerable<IWorldPartitionManager> FindPartitionsForPosition(int x, int y, float radius)
        {
            float minX = x - radius;
            float maxX = x + radius;
            float minY = y - radius;
            float maxY = y + radius;

            return _partitions.Where(p =>
                maxX >= p.Position.X &&
                minX <= p.Position.X + p.Size.X &&
                maxY >= p.Position.Y &&
                minY <= p.Position.Y + p.Size.Y
            );
        }

        private IEnumerable<IWorldPartitionManager> AddObjectToPartitions(
            IWorldObject2D obj,
            IEnumerable<IWorldPartitionManager> partitions
        )
        {
            foreach (var partition in partitions)
            {
                if (partition is WorldPartition2D p2d)
                {
                    p2d.AddObject(obj);
                }
            }

            return partitions;
        }

        /// <summary>
        /// Compute a partition search radius from the object's transform size.
        /// Uses half of the largest dimension; minimal floor if degenerate.
        /// </summary>
        private static float ComputePartitionRadius(IWorldObject2D obj)
        {
            var sz = obj.Transform.Size;
            var r = MathF.Max(sz.X, sz.Y) * 0.5f;

            if (r <= 0f || float.IsNaN(r) || float.IsInfinity(r))
                r = 0.5f; // minimal sensible radius

            return r;
        }
    }
}
