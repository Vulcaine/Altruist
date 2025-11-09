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
        void Initialize();
        Task SaveAsync();
        IEnumerable<IWorldPartitionManager> UpdateObjectPosition(IPrefab2D prefab, float radius);
        void AddDynamicObject(IPrefab2D prefab, float radius);
        IWorldPartitionManager? AddStaticObject(IPrefab2D prefab);
        IPrefab2D? DestroyObject(string instanceId);
        IEnumerable<IPrefab2D> GetNearbyObjectsInRoom(string prefabId, int x, int y, float radius, string roomId);
        IEnumerable<IWorldPartitionManager> FindPartitionsForPosition(int x, int y, float radius);
        IWorldPartitionManager? FindPartitionForPosition(int x, int y);
    }

    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "2D")]
    [Service(typeof(IGameWorldManager))]
    public sealed class GameWorldManager2D : IGameWorldManager2D
    {
        private readonly WorldIndex2D _index;
        private readonly IWorldPartitioner2D _worldPartitioner;
        private readonly ICacheProvider _cache;
        private readonly Dictionary<PartitionIndex2D, IWorldPartitionManager> _partitionMap = new();

        private readonly List<WorldPartition2D> _partitions;
        private readonly IPhysxWorld _physx2D;

        public GameWorldManager2D(WorldIndex2D world, IPhysxWorld2D physx2D, IWorldPartitioner2D worldPartitioner, ICacheProvider cacheProvider)
        {
            _index = world ?? throw new ArgumentNullException(nameof(world));
            _worldPartitioner = worldPartitioner ?? throw new ArgumentNullException(nameof(worldPartitioner));
            _cache = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));

            _physx2D = physx2D ?? throw new ArgumentNullException(nameof(physx2D));
            _partitions = new List<WorldPartition2D>();
        }

        public IPhysxWorld PhysxWorld => _physx2D;

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

        public IEnumerable<IWorldPartitionManager> UpdateObjectPosition(IPrefab2D prefab, float radius)
        {
            DestroyObject(prefab);
            var partitions = FindPartitionsForPosition(prefab.Transform.Position.X, prefab.Transform.Position.Y, radius);
            AddObjectToPartitions(prefab, partitions);
            return partitions;
        }

        public void AddDynamicObject(IPrefab2D prefab, float radius)
        {
            var partitions = FindPartitionsForPosition(prefab.Transform.Position.X, prefab.Transform.Position.Y, radius);
            AddObjectToPartitions(prefab, partitions);
        }

        public IWorldPartitionManager? AddStaticObject(IPrefab2D prefab)
        {
            var partition = FindPartitionForPosition(prefab.Transform.Position.X, prefab.Transform.Position.Y);
            if (partition is WorldPartition2D p2d)
            {
                p2d.AddObject(prefab);
            }
            return partition;
        }

        public IPrefab2D? DestroyObject(string instanceId)
            => _partitions.Select(p => p.DestroyObject(instanceId)).FirstOrDefault(m => m != null);

        public IPrefab2D? DestroyObject(IPrefab2D prefab)
       => DestroyObject(prefab.InstanceId);

        public IEnumerable<IPrefab2D> GetNearbyObjectsInRoom(string prefabId, int x, int y, float radius, string roomId)
        {
            var result = new List<IPrefab2D>();
            var partitions = FindPartitionsForPosition(x, y, radius);
            foreach (var partition in partitions)
            {
                if (partition is WorldPartition2D p2d)
                {
                    result.AddRange(p2d.GetObjectsByTypeInRadius(prefabId, x, y, radius, roomId));
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
            {
                return maxX >= p.Position.X &&
                       minX <= p.Position.X + p.Size.X &&
                       maxY >= p.Position.Y &&
                       minY <= p.Position.Y + p.Size.Y;
            });
        }

        private IEnumerable<IWorldPartitionManager> AddObjectToPartitions(
            IPrefab2D prefab,
            IEnumerable<IWorldPartitionManager> partitions
        )
        {
            foreach (var partition in partitions)
            {
                if (partition is WorldPartition2D p2d)
                {
                    p2d.AddObject(prefab);
                }
            }

            return partitions;
        }
    }
}
