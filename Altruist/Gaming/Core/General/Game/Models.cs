using System;
using System.Text.Json.Serialization;

namespace Altruist.Gaming;

public class WorldSettings
{
    public int Width { get; set; }
    public int Height { get; set; }

    public WorldSettings(int width, int height)
    {
        Width = width;
        Height = height;
    }
}

public interface IWorldPartitioner
{
    int PartitionWidth { get; }
    int PartitionHeight { get; }
    List<Partition> CalculatePartitions(WorldSettings world);
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

    public List<Partition> CalculatePartitions(WorldSettings world)
    {
        var partitions = new List<Partition>();

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

                var partition = new Partition
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

public class ObjectMetadata
{
    public WorldObjectTypeKey Type { get; set; } = default!;
    public string InstanceId { get; set; } = "";
    public (int X, int Y) Position { get; set; }
}

public record WorldObjectTypeKey(string Value);

public static class WorldObjectTypeKeys
{
    public static readonly WorldObjectTypeKey Client = new("client");
    public static readonly WorldObjectTypeKey Item = new("item");
}

public class ObjectTypeMap
{
    private readonly Dictionary<WorldObjectTypeKey, Dictionary<string, ObjectMetadata>> _data = new();

    public void Add(WorldObjectTypeKey type, ObjectMetadata objectMetadata)
    {
        if (!_data.TryGetValue(type, out var set))
        {
            set = new Dictionary<string, ObjectMetadata>();
            _data[type] = set;
        }
        set.Add(objectMetadata.InstanceId, objectMetadata);
    }

    public ObjectMetadata? Remove(WorldObjectTypeKey type, string id)
    {
        if (_data.TryGetValue(type, out var set))
        {
            set.TryGetValue(id, out var metadata);
            set.Remove(id);
            if (set.Count == 0) _data.Remove(type);
            return metadata;
        }

        return null;
    }

    public HashSet<string> GetByType(WorldObjectTypeKey type) =>
        _data.TryGetValue(type, out var set) ? set.Keys.ToHashSet() : new HashSet<string>();

    public Dictionary<WorldObjectTypeKey, Dictionary<string, ObjectMetadata>> GetAll() =>
        _data.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
}

public class SpatialGridIndex
{
    public int CellSize { get; set; }

    // Use stringified keys like "x:y" to allow JSON serialization
    public Dictionary<string, List<ObjectMetadata>> Grid { get; set; } = new();

    // Optional, flatten all objects by ID if needed
    public Dictionary<string, ObjectMetadata> InstanceMap { get; set; } = new();

    // Optional, used for type filtering
    public Dictionary<string, Dictionary<string, ObjectMetadata>> TypeMap { get; set; } = new();

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
            Grid[key] = list = new List<ObjectMetadata>();

        list.Add(obj);
        InstanceMap[obj.InstanceId] = obj;

        var typeKey = type.ToString();
        if (!TypeMap.TryGetValue(typeKey, out var typeDict))
            TypeMap[typeKey] = typeDict = new Dictionary<string, ObjectMetadata>();

        typeDict[obj.InstanceId] = obj;
    }

    public ObjectMetadata? Remove(WorldObjectTypeKey type, string instanceId)
    {
        if (!InstanceMap.TryGetValue(instanceId, out var obj))
            return null;

        string key = GetKey(obj.Position.X / CellSize, obj.Position.Y / CellSize);
        Grid[key]?.Remove(obj);

        TypeMap[type.ToString()]?.Remove(instanceId);
        InstanceMap.Remove(instanceId);

        return obj;
    }

    public IEnumerable<ObjectMetadata> Query(WorldObjectTypeKey type, int x, int y, float radius)
    {
        int minX = (int)((x - radius) / CellSize);
        int maxX = (int)((x + radius) / CellSize);
        int minY = (int)((y - radius) / CellSize);
        int maxY = (int)((y + radius) / CellSize);

        float sqrRadius = radius * radius;
        var result = new List<ObjectMetadata>();
        var typeKey = type.ToString();

        for (int cx = minX; cx <= maxX; cx++)
        {
            for (int cy = minY; cy <= maxY; cy++)
            {
                string key = GetKey(cx, cy);
                if (!Grid.TryGetValue(key, out var list)) continue;

                foreach (var obj in list)
                {
                    if (obj.Type.ToString() != typeKey) continue;

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
        return TypeMap.TryGetValue(type.Value, out var map) ? map : new();
    }

    public HashSet<ObjectMetadata> GetAllByType(WorldObjectTypeKey type)
    {
        return GetByType(type).Values.ToHashSet();
    }
}

public class Partition
{
    private readonly SpatialGridIndex _spatialIndex = new(cellSize: 16);
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public (int X, int Y) Index { get; set; }
    public (int X, int Y) Position { get; set; }
    public (int Width, int Height) Size { get; set; }
    public (int X, int Y) Epicenter { get; set; }


    public void AddObject(WorldObjectTypeKey objectType, ObjectMetadata objectMetadata)
    {
        _spatialIndex.Add(objectType, objectMetadata);
    }

    public ObjectMetadata? DestroyObject(WorldObjectTypeKey objectType, string id)
    {
        return _spatialIndex.Remove(objectType, id);
    }

    public IEnumerable<ObjectMetadata> GetObjectsByTypeInRadius(WorldObjectTypeKey objectType, int x, int y, float radius)
    {
        return _spatialIndex.Query(objectType, x, y, radius);
    }

    public HashSet<ObjectMetadata> GetObjectsByType(WorldObjectTypeKey objectType) =>
        _spatialIndex.GetAllByType(objectType);
}
