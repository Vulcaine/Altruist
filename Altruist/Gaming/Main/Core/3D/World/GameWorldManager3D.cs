/* 
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.ThreeD
{
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "3D")]
    [Service(typeof(IGameWorldManager))]
    public sealed class GameWorldManager3D : IGameWorldManager
    {
        private readonly WorldIndex3D _index;
        private readonly IWorldPartitioner3D _worldPartitioner;
        private readonly ICacheProvider _cache;
        private readonly Dictionary<PartitionIndex3D, WorldPartition3D> _partitionMap = new();

        private readonly List<WorldPartition3D> _partitions;
        private readonly IPhysxWorld3D _physx3D;

        public GameWorldManager3D(WorldIndex3D world, IPhysxWorld3D physx3D, IWorldPartitioner3D worldPartitioner, ICacheProvider cacheProvider)
        {
            _index = world ?? throw new ArgumentNullException(nameof(world));
            _worldPartitioner = worldPartitioner ?? throw new ArgumentNullException(nameof(worldPartitioner));
            _cache = cacheProvider ?? throw new ArgumentNullException(nameof(cacheProvider));

            _physx3D = physx3D ?? throw new ArgumentNullException(nameof(physx3D));
            _partitions = new List<WorldPartition3D>();
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

            _ = SaveAsync();
        }

        public async Task SaveAsync()
        {
            var saveTasks = _partitions.Select(p => _cache.SaveAsync(p.SysId, p));
            await Task.WhenAll(saveTasks);
        }

        public IEnumerable<WorldPartition3D> UpdateObjectPosition(WorldObjectTypeKey objectType, ObjectMetadata3D ObjectMetadata3D, float radius)
        {
            DestroyObject(objectType, ObjectMetadata3D.InstanceId);
            var partitions = FindPartitionsForPosition(ObjectMetadata3D.Position.X, ObjectMetadata3D.Position.Y, ObjectMetadata3D.Position.Z, radius);
            AddObjectToPartitions(objectType, ObjectMetadata3D, partitions);
            return partitions;
        }

        public void AddDynamicObject(WorldObjectTypeKey objectType, ObjectMetadata3D ObjectMetadata3D, float radius)
        {
            var partitions = FindPartitionsForPosition(ObjectMetadata3D.Position.X, ObjectMetadata3D.Position.Y, ObjectMetadata3D.Position.Z, radius);
            AddObjectToPartitions(objectType, ObjectMetadata3D, partitions);
        }

        public WorldPartition3D? AddStaticObject(WorldObjectTypeKey objectType, ObjectMetadata3D ObjectMetadata3D)
        {
            var partition = FindPartitionForPosition(ObjectMetadata3D.Position.X, ObjectMetadata3D.Position.Y, ObjectMetadata3D.Position.Z);
            partition?.AddObject(objectType, ObjectMetadata3D);
            return partition;
        }

        public IObjectMetadata? DestroyObject(WorldObjectTypeKey objectType, string instanceId)
            => _partitions.Select(p => p.DestroyObject(objectType, instanceId)).FirstOrDefault(m => m != null);

        public IEnumerable<IObjectMetadata> GetNearbyObjectsInRoom(WorldObjectTypeKey objectType, int x, int y, int z, float radius, string roomId)
        {
            var result = new List<IObjectMetadata>();
            var partitions = FindPartitionsForPosition(x, y, z, radius);
            foreach (var partition in partitions)
                result.AddRange(partition.GetObjectsByTypeInRadius(objectType, x, y, z, radius, roomId));

            return result.Distinct();
        }

        public WorldPartition3D? FindPartitionForPosition(int x, int y, int z)
        {
            int indexX = (int)Math.Round(x / (double)_worldPartitioner.PartitionWidth);
            int indexY = (int)Math.Round(y / (double)_worldPartitioner.PartitionHeight);
            int indexZ = (int)Math.Round(z / (double)_worldPartitioner.PartitionDepth);

            return _partitionMap.TryGetValue(new PartitionIndex3D(indexX, indexY, indexZ), out var p) ? p : null;
        }

        public IEnumerable<WorldPartition3D> FindPartitionsForPosition(int x, int y, int z, float radius)
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


        private IEnumerable<WorldPartition3D> AddObjectToPartitions(WorldObjectTypeKey objectType, ObjectMetadata3D ObjectMetadata3D, IEnumerable<WorldPartition3D> partitions)
        {
            foreach (var partition in partitions)
                partition.AddObject(objectType, ObjectMetadata3D);
            return partitions;
        }
    }
}
