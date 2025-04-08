
using Altruist.UORM;

namespace Altruist.Gaming;

[Table("world_index")]
public class WorldIndex : IVaultModel
{
    public int Index { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public string Type { get; set; } = "WorldIndex";

    public WorldIndex(int index, int width, int height)
    {
        Index = index;
        Width = width;
        Height = height;
    }
}

public interface IWorldPartitioner
{
    int PartitionWidth { get; }
    int PartitionHeight { get; }
    List<WorldPartition> CalculatePartitions(WorldIndex world);
}

public class WorldPartitioner : IWorldPartitioner
{
    public int PartitionWidth { get; }
    public int PartitionHeight { get; }

    public WorldPartitioner(int partitionWidth, int partitionHeight)
    {
        PartitionWidth = partitionWidth;
        PartitionHeight = partitionHeight;
    }

    public List<WorldPartition> CalculatePartitions(WorldIndex world)
    {
        var partitions = new List<WorldPartition>();

        int columns = (int)Math.Ceiling((double)world.Width / PartitionWidth);
        int rows = (int)Math.Ceiling((double)world.Height / PartitionHeight);

        for (int row = 0; row < rows; row++)
        {
            for (int col = 0; col < columns; col++)
            {
                int x = col * PartitionWidth;
                int y = row * PartitionHeight;

                int width = Math.Min(PartitionWidth, world.Width - x);
                int height = Math.Min(PartitionHeight, world.Height - y);

                var partition = new WorldPartition
                {
                    Index = (col, row),
                    Position = (x, y),
                    Size = (width, height),
                    Epicenter = (x + width / 2, y + height / 2)
                };

                partitions.Add(partition);
            }
        }

        return partitions;
    }
}

public abstract class WorldObject
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public int X { get; set; }
    public int Y { get; set; }
}


public class PartitionIndex
{
    public int X { get; }
    public int Y { get; }

    public PartitionIndex(int x, int y)
    {
        X = x;
        Y = y;
    }

    public override bool Equals(object? obj) =>
        obj is PartitionIndex other && X == other.X && Y == other.Y;

    public override int GetHashCode() => HashCode.Combine(X, Y);
}

/// <summary>
/// Represents metadata about a world object, including its type, position,
/// instance identifier, associated room, and nearby/connected clients.
/// </summary>
public class ObjectMetadata
{
    /// <summary>
    /// The type of the world object (e.g., player, NPC, item).
    /// </summary>
    public WorldObjectTypeKey Type { get; set; } = default!;

    /// <summary>
    /// A unique identifier for this specific instance of the object.
    /// </summary>
    public string InstanceId { get; set; } = "";

    /// <summary>
    /// The identifier of the room this object belongs to.
    /// </summary>
    public string RoomId { get; set; } = "";

    /// <summary>
    /// A set of client IDs that have received this object.
    /// 
    /// This is used to:
    /// - Track which connected clients are aware of this object,
    /// - Determine which clients should be notified when the object is removed,
    /// - Track client proximity or visibility to this object.
    /// </summary>
    public HashSet<string> ReceiverClientIds { get; set; } = new();

    /// <summary>
    /// The (X, Y) position of the object in the world/grid.
    /// </summary>
    public (int X, int Y) Position { get; set; }
}


public record WorldObjectTypeKey(string Value);

public static class WorldObjectTypeKeys
{
    public static readonly WorldObjectTypeKey Client = new("client");
    public static readonly WorldObjectTypeKey Item = new("item");
}

public class SpatialGridIndex
{
    public int CellSize { get; set; }

    // Use stringified keys like "x:y" to allow JSON serialization
    // Optional, flatten all objects by ID if needed
    public Dictionary<string, ObjectMetadata> InstanceMap { get; set; } = new();

    // grid key => metadata id string
    public Dictionary<string, HashSet<string>> Grid { get; set; } = new();

    // Optional, used for type filtering, type string => metadata id string
    public Dictionary<string, HashSet<string>> TypeMap { get; set; } = new();

    public SpatialGridIndex() { }

    public SpatialGridIndex(int cellSize)
    {
        CellSize = cellSize;
    }

    private static string GetKey(int x, int y) => $"{x}:{y}";

    public void Add(WorldObjectTypeKey type, ObjectMetadata obj)
    {
        string key = GetKey(obj.Position.X / CellSize, obj.Position.Y / CellSize);

        if (!Grid.TryGetValue(key, out var list))
            Grid[key] = list = new HashSet<string>();

        list.Add(obj.InstanceId);
        InstanceMap[obj.InstanceId] = obj;

        var typeKey = type.Value;
        if (!TypeMap.TryGetValue(typeKey, out var typeDict))
            TypeMap[typeKey] = typeDict = new HashSet<string>();

        typeDict.Add(obj.InstanceId);
    }

    public ObjectMetadata? Remove(WorldObjectTypeKey type, string instanceId)
    {
        if (!InstanceMap.TryGetValue(instanceId, out var obj))
            return null;

        string key = GetKey(obj.Position.X / CellSize, obj.Position.Y / CellSize);
        if (Grid.TryGetValue(key, out var list))
        {
            list.Remove(instanceId);
        }

        if (TypeMap.TryGetValue(type.Value, out var map))
        {
            map.Remove(instanceId);
        }

        InstanceMap.Remove(instanceId);

        return obj;
    }

    public IEnumerable<ObjectMetadata> Query(WorldObjectTypeKey type, int x, int y, float radius, string roomId)
    {
        int minX = (int)((x - radius) / CellSize);
        int maxX = (int)((x + radius) / CellSize);
        int minY = (int)((y - radius) / CellSize);
        int maxY = (int)((y + radius) / CellSize);

        float sqrRadius = radius * radius;
        var result = new HashSet<ObjectMetadata>();

        for (int cx = minX; cx <= maxX; cx++)
        {
            for (int cy = minY; cy <= maxY; cy++)
            {
                string key = GetKey(cx, cy);
                if (!Grid.TryGetValue(key, out var list)) continue;

                var instanceList = list.Select(e => InstanceMap[e]).Where(e => e.RoomId == roomId).ToList();

                foreach (var obj in instanceList)
                {
                    if (obj.Type != type) continue;

                    float dx = obj.Position.X - x;
                    float dy = obj.Position.Y - y;

                    if ((dx * dx + dy * dy) <= sqrRadius)
                        result.Add(obj);
                }
            }
        }

        return result;
    }

    public Dictionary<string, ObjectMetadata> GetByType(WorldObjectTypeKey type)
    {
        return (TypeMap.TryGetValue(type.Value, out var map) ? map : new()).ToDictionary(x => x, x => InstanceMap[x]);
    }

    public HashSet<ObjectMetadata> GetAllByType(WorldObjectTypeKey type)
    {
        return GetByType(type).Values.ToHashSet();
    }
}

public class WorldPartition : IModel
{
    private readonly SpatialGridIndex _spatialIndex = new(cellSize: 16);
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public (int X, int Y) Index { get; set; }
    public (int X, int Y) Position { get; set; }
    public (int Width, int Height) Size { get; set; }
    public (int X, int Y) Epicenter { get; set; }
    public string Type { get; set; } = "WorldPartition";

    public void AddObject(WorldObjectTypeKey objectType, ObjectMetadata objectMetadata)
    {
        _spatialIndex.Add(objectType, objectMetadata);
    }

    public ObjectMetadata? DestroyObject(WorldObjectTypeKey objectType, string id)
    {
        return _spatialIndex.Remove(objectType, id);
    }

    public IEnumerable<ObjectMetadata> GetObjectsByTypeInRadius(WorldObjectTypeKey objectType, int x, int y, float radius, string roomId)
    {
        return _spatialIndex.Query(objectType, x, y, radius, roomId);
    }

    public HashSet<ObjectMetadata> GetObjectsByType(WorldObjectTypeKey objectType) =>
        _spatialIndex.GetAllByType(objectType);

    public HashSet<ObjectMetadata> GetObjectsByTypeInRoom(WorldObjectTypeKey objectType, string roomId) =>
        _spatialIndex.GetAllByType(objectType).Where(x => x.RoomId == roomId).ToHashSet();
}
