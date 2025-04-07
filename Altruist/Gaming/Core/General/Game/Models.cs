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
    public string ObjectId { get; set; } = "";
    public (int X, int Y) Position { get; set; }
}

public record ObjectTypeKey(string Value);

public static class ObjectTypeKeys
{
    public static readonly ObjectTypeKey Client = new("client");
}

public class ObjectTypeMap
{
    private readonly Dictionary<ObjectTypeKey, Dictionary<string, ObjectMetadata>> _data = new();

    public void Add(ObjectTypeKey type, ObjectMetadata objectMetadata)
    {
        if (!_data.TryGetValue(type, out var set))
        {
            set = new Dictionary<string, ObjectMetadata>();
            _data[type] = set;
        }
        set.Add(objectMetadata.ObjectId, objectMetadata);
    }

    public void Remove(ObjectTypeKey type, string id)
    {
        if (_data.TryGetValue(type, out var set))
        {
            set.Remove(id);
            if (set.Count == 0) _data.Remove(type);
        }
    }

    public HashSet<string> GetByType(ObjectTypeKey type) =>
        _data.TryGetValue(type, out var set) ? set.Keys.ToHashSet() : new HashSet<string>();

    public Dictionary<ObjectTypeKey, Dictionary<string, ObjectMetadata>> GetAll() =>
        _data.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
}



public class Partition
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    public (int X, int Y) Index { get; set; }
    public (int X, int Y) Position { get; set; }
    public (int Width, int Height) Size { get; set; }
    public (int X, int Y) Epicenter { get; set; }

    private readonly ObjectTypeMap _objectMap = new();

    public void AddObject(ObjectTypeKey objectType, ObjectMetadata objectMetadata)
    {
        _objectMap.Add(objectType, objectMetadata);
    }

    public void RemoveObject(ObjectTypeKey objectType, string id)
    {
        _objectMap.Remove(objectType, id);
    }

    public HashSet<string> GetObjectIdsByType(ObjectTypeKey objectType)
    {
        return _objectMap.GetByType(objectType);
    }

    public Dictionary<ObjectTypeKey, Dictionary<string, ObjectMetadata>> GetAllMetadata()
    {
        return _objectMap.GetAll();
    }
}
