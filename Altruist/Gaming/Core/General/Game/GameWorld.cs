
namespace Altruist.Gaming;

public class GameWorldManager
{
    protected readonly WorldIndex _world;
    protected readonly List<WorldPartition> _partitions;
    protected readonly ICacheProvider _cache;

    private readonly IWorldPartitioner _worldPartitioner;
    private readonly Dictionary<PartitionIndex, WorldPartition> _partitionMap = new();

    public GameWorldManager(WorldIndex world, IWorldPartitioner worldPartitioner, ICacheProvider cacheProvider)
    {
        _world = world;
        _partitions = new List<WorldPartition>();
        _cache = cacheProvider;
        _worldPartitioner = worldPartitioner;
    }

    public virtual void Initialize()
    {
        var partitions = _worldPartitioner.CalculatePartitions(_world);
        foreach (var partition in partitions)
        {
            _partitions.Add(partition);
            _partitionMap[new PartitionIndex(partition.Index.X, partition.Index.Y)] = partition;
        }

        _ = SaveAsync();
    }

    public virtual async Task SaveAsync()
    {
        var saveTasks = _partitions.Select(p => _cache.SaveAsync(p.GenId, p));
        await Task.WhenAll(saveTasks);
    }

    public virtual IEnumerable<WorldPartition> UpdateObjectPosition(WorldObjectTypeKey objectType, ObjectMetadata objectMetadata, float radius)
    {
        DestroyObject(objectType, objectMetadata.InstanceId);
        var partitions = FindPartitionsForPosition(objectMetadata.Position.X, objectMetadata.Position.Y, radius);
        AddObjectToPartitions(objectType, objectMetadata, partitions);
        return partitions;
    }

    private IEnumerable<WorldPartition> AddObjectToPartitions(WorldObjectTypeKey objectType, ObjectMetadata objectMetadata, IEnumerable<WorldPartition> partitions)
    {
        foreach (var partition in partitions)
        {
            partition.AddObject(objectType, objectMetadata);
        }
        return partitions;
    }

    public virtual void AddDynamicObject(WorldObjectTypeKey objectType, ObjectMetadata objectMetadata, float radius)
    {
        var partitions = FindPartitionsForPosition(objectMetadata.Position.X, objectMetadata.Position.Y, radius);
        AddObjectToPartitions(objectType, objectMetadata, partitions);
    }

    public virtual WorldPartition? AddStaticObject(WorldObjectTypeKey objectType, ObjectMetadata objectMetadata)
    {
        var partition = FindPartitionForPosition(objectMetadata.Position.X, objectMetadata.Position.Y);
        partition?.AddObject(objectType, objectMetadata);
        return partition;
    }

    public virtual ObjectMetadata? DestroyObject(WorldObjectTypeKey objectType, string instanceId)
    {
        return _partitions.Select(p => p.DestroyObject(objectType, instanceId)).FirstOrDefault(m => m != null);
    }

    /// <summary>
    /// Retrieves the IDs of objects of the specified type within a given radius of a position.
    /// First filters by partitions, then filters individual object positions for accurate proximity.
    /// </summary>
    /// <param name="objectType">Type of objects to search for.</param>
    /// <param name="x">X coordinate of the center point.</param>
    /// <param name="y">Y coordinate of the center point.</param>
    /// <param name="radius">Radius around the point to search.</param>
    /// <returns>List of object instance IDs within the specified radius.</returns>
    public virtual IEnumerable<ObjectMetadata> GetNearbyObjectsInRoom(WorldObjectTypeKey objectType, int x, int y, float radius, string roomId)
    {
        var result = new List<ObjectMetadata>();
        var partitions = FindPartitionsForPosition(x, y, radius);
        foreach (var partition in partitions)
        {
            result.AddRange(partition.GetObjectsByTypeInRadius(objectType, x, y, radius, roomId));
        }

        return result.Distinct();
    }

    public virtual WorldPartition? FindPartitionForPosition(int x, int y)
    {
        // Round the division result to the nearest integer and convert to int
        int indexX = (int)Math.Round(x / (double)_worldPartitioner.PartitionWidth);
        int indexY = (int)Math.Round(y / (double)_worldPartitioner.PartitionHeight);

        return _partitionMap.TryGetValue(new PartitionIndex(indexX, indexY), out var p) ? p : null;
    }

    /// <summary>
    /// Finds partitions that intersect with a circular area defined by position and radius.
    /// </summary>
    /// <param name="x">Center X coordinate.</param>
    /// <param name="y">Center Y coordinate.</param>
    /// <param name="radius">Radius of the area.</param>
    /// <returns>List of partitions intersecting the given area.</returns>
    public virtual IEnumerable<WorldPartition> FindPartitionsForPosition(int x, int y, float radius)
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
}


public class GameWorldCoordinator
{
    private readonly Dictionary<int, GameWorldManager> _worlds = new();
    private readonly IWorldPartitioner _partitioner;
    private readonly ICacheProvider _cache;

    public GameWorldCoordinator(IWorldPartitioner partitioner, ICacheProvider cache)
    {
        _partitioner = partitioner;
        _cache = cache;
    }

    /// <summary>
    /// Adds a new game world and initializes it.
    /// </summary>
    public virtual void AddWorld(WorldIndex index)
    {
        if (_worlds.ContainsKey(index.Index))
            throw new InvalidOperationException($"World {index.Index} already exists.");

        var manager = new GameWorldManager(index, _partitioner, _cache);
        manager.Initialize();
        _worlds[index.Index] = manager;
    }

    /// <summary>
    /// Removes the specified world by index.
    /// </summary>
    public virtual void RemoveWorld(int index)
    {
        _worlds.Remove(index);
    }

    /// <summary>
    /// Gets the GameWorldManager for a given world index.
    /// </summary>
    public virtual GameWorldManager? GetWorld(int index)
    {
        return _worlds.TryGetValue(index, out var manager) ? manager : null;
    }

    /// <summary>
    /// Lists all currently loaded world indices.
    /// </summary>
    public virtual IEnumerable<int> GetAllWorldIndices() => _worlds.Keys;
}
