/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;

namespace Altruist.Gaming.TwoD
{
    public sealed class GameWorldManager2D
    {
        private readonly WorldIndex2D _index;
        private readonly IWorldPartitioner2D _worldPartitioner;
        private readonly ICacheProvider _cache;
        private readonly Dictionary<PartitionIndex2D, WorldPartition2D> _partitionMap = new();

        private readonly List<WorldPartition2D> _partitions;
        private readonly IPhysxWorld2D _physx2D;

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
            var saveTasks = _partitions.Select(p => _cache.SaveAsync(p.SysId, p));
            await Task.WhenAll(saveTasks);
        }

        public IEnumerable<WorldPartition2D> UpdateObjectPosition(WorldObjectTypeKey objectType, ObjectMetadata2D objectMetadata, float radius)
        {
            DestroyObject(objectType, objectMetadata.InstanceId);
            var partitions = FindPartitionsForPosition(objectMetadata.Position.X, objectMetadata.Position.Y, radius);
            AddObjectToPartitions(objectType, objectMetadata, partitions);
            return partitions;
        }

        public void AddDynamicObject(WorldObjectTypeKey objectType, ObjectMetadata2D objectMetadata, float radius)
        {
            var partitions = FindPartitionsForPosition(objectMetadata.Position.X, objectMetadata.Position.Y, radius);
            AddObjectToPartitions(objectType, objectMetadata, partitions);
        }

        public WorldPartition2D? AddStaticObject(WorldObjectTypeKey objectType, ObjectMetadata2D objectMetadata)
        {
            var partition = FindPartitionForPosition(objectMetadata.Position.X, objectMetadata.Position.Y);
            partition?.AddObject(objectType, objectMetadata);
            return partition;
        }

        public ObjectMetadata2D? DestroyObject(WorldObjectTypeKey objectType, string instanceId)
            => _partitions.Select(p => p.DestroyObject(objectType, instanceId)).FirstOrDefault(m => m != null);

        public IEnumerable<ObjectMetadata2D> GetNearbyObjectsInRoom(WorldObjectTypeKey objectType, int x, int y, float radius, string roomId)
        {
            var result = new List<ObjectMetadata2D>();
            var partitions = FindPartitionsForPosition(x, y, radius);
            foreach (var partition in partitions)
                result.AddRange(partition.GetObjectsByTypeInRadius(objectType, x, y, radius, roomId));

            return result.Distinct();
        }

        public WorldPartition2D? FindPartitionForPosition(int x, int y)
        {
            int indexX = (int)Math.Round(x / (double)_worldPartitioner.PartitionWidth);
            int indexY = (int)Math.Round(y / (double)_worldPartitioner.PartitionHeight);

            return _partitionMap.TryGetValue(new PartitionIndex2D(indexX, indexY), out var p) ? p : null;
        }

        public IEnumerable<WorldPartition2D> FindPartitionsForPosition(int x, int y, float radius)
        {
            float minX = x - radius;
            float maxX = x + radius;
            float minY = y - radius;
            float maxY = y + radius;

            return _partitions.Where(partition =>
                maxX >= partition.Position.X &&
                minX <= partition.Position.X + partition.Size.X &&
                maxY >= partition.Position.Y &&
                minY <= partition.Position.Y + partition.Size.Y
            );
        }

        private IEnumerable<WorldPartition2D> AddObjectToPartitions(WorldObjectTypeKey objectType, ObjectMetadata2D objectMetadata, IEnumerable<WorldPartition2D> partitions)
        {
            foreach (var partition in partitions)
                partition.AddObject(objectType, objectMetadata);
            return partitions;
        }
    }
}
