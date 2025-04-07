
using System.Runtime.CompilerServices;

namespace Altruist.Gaming;

public class GameWorld
{
    protected readonly WorldSettings _world;
    protected readonly List<Partition> _partitions;
    protected readonly ICacheProvider _cache;

    private readonly IWorldPartitioner _worldPartitioner;
    private readonly Dictionary<PartitionIndex, Partition> _partitionMap = new();

    public GameWorld(WorldSettings world, IWorldPartitioner worldPartitioner, ICacheProvider cacheProvider)
    {
        _world = world;
        _partitions = new List<Partition>();
        _cache = cacheProvider;
        _worldPartitioner = worldPartitioner;
    }

    public void Initialize()
    {
        var partitions = _worldPartitioner.CalculatePartitions(_world);
        foreach (var partition in partitions)
        {
            _partitions.Add(partition);
            _partitionMap[new PartitionIndex(partition.Index.X, partition.Index.Y)] = partition;
        }

        _ = SaveAsync();
    }

    public async Task SaveAsync()
    {
        var saveTasks = _partitions.Select(p => _cache.SaveAsync(p.Id, p));
        await Task.WhenAll(saveTasks);
    }

    public List<Partition> UpdateObjectPosition(ObjectTypeKey objectType, ObjectMetadata objectMetadata, float radius)
    {
        RemoveObject(objectType, objectMetadata.ObjectId);
        var partitions = FindPartitionsForPosition(objectMetadata.Position.X, objectMetadata.Position.Y, radius);
        AddObjectToPartitions(objectType, objectMetadata, partitions);
        return partitions;
    }

    private List<Partition> AddObjectToPartitions(ObjectTypeKey objectType, ObjectMetadata objectMetadata, List<Partition> partitions)
    {
        foreach (var partition in partitions)
        {
            partition.AddObject(objectType, objectMetadata);
        }
        return partitions;
    }

    public void AddDynamicObject(ObjectTypeKey objectType, ObjectMetadata objectMetadata, float radius)
    {
        var partitions = FindPartitionsForPosition(objectMetadata.Position.X, objectMetadata.Position.Y, radius);
        AddObjectToPartitions(objectType, objectMetadata, partitions);
    }

    public Partition? AddStaticObject(ObjectTypeKey objectType, ObjectMetadata objectMetadata)
    {
        var partition = FindPartitionForPosition(objectMetadata.Position.X, objectMetadata.Position.Y);
        partition?.AddObject(objectType, objectMetadata);
        return partition;
    }

    public void RemoveObject(ObjectTypeKey objectType, string instanceId)
    {
        _partitions.ForEach(p => p.RemoveObject(objectType, instanceId));
    }

    public List<string> GetNearbyObjectIds(ObjectTypeKey objectType, int x, int y, float radius)
    {
        var nearbyPartitions = FindPartitionsForPosition(x, y, radius);
        return nearbyPartitions
            .SelectMany(p => p.GetObjectIdsByType(objectType))
            .Distinct()
            .ToList();
    }

    public Partition? FindPartitionForPosition(int x, int y)
    {
        int indexX = x / _worldPartitioner.PartitionWidth;
        int indexY = y / _worldPartitioner.PartitionHeight;
        return _partitionMap.TryGetValue(new PartitionIndex(indexX, indexY), out var p) ? p : null;
    }

    public List<Partition> FindPartitionsForPosition(int x, int y, float radius)
    {
        var result = new List<Partition>();

        float minX = x - radius;
        float maxX = x + radius;
        float minY = y - radius;
        float maxY = y + radius;

        foreach (var partition in _partitions)
        {
            bool intersects =
                maxX >= partition.Position.X &&
                minX <= partition.Position.X + partition.Size.Width &&
                maxY >= partition.Position.Y &&
                minY <= partition.Position.Y + partition.Size.Height;

            if (intersects)
                result.Add(partition);
        }

        return result;
    }
}
