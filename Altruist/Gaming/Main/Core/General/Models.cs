/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Numerics;
using System.Text.Json.Serialization;
using Altruist.Networking;
using Altruist.UORM;
using Box2DSharp.Collision.Shapes;
using Box2DSharp.Dynamics;
using MessagePack;

namespace Altruist.Gaming;

public struct IntVector2
{
    public int X { get; set; }
    public int Y { get; set; }

    public IntVector2(int x, int y)
    {
        X = x;
        Y = y;
    }

    // You can add operators, methods, etc. as needed
    public static IntVector2 operator +(IntVector2 a, IntVector2 b)
    {
        return new IntVector2(a.X + b.X, a.Y + b.Y);
    }

    public static IntVector2 operator -(IntVector2 a, IntVector2 b)
    {
        return new IntVector2(a.X - b.X, a.Y - b.Y);
    }

    public static IntVector2 operator *(IntVector2 v, int scalar)
    {
        return new IntVector2(v.X * scalar, v.Y * scalar);
    }

    public static IntVector2 operator /(IntVector2 v, int scalar)
    {
        return new IntVector2(v.X / scalar, v.Y / scalar);
    }

    public override string ToString()
    {
        return $"({X}, {Y})";
    }
}

public struct ByteVector2
{
    public byte X { get; set; }
    public byte Y { get; set; }

    public ByteVector2(byte x, byte y)
    {
        X = x;
        Y = y;
    }

    // Addition operator
    public static ByteVector2 operator +(ByteVector2 a, ByteVector2 b)
    {
        return new ByteVector2((byte)(a.X + b.X), (byte)(a.Y + b.Y));
    }

    // Subtraction operator
    public static ByteVector2 operator -(ByteVector2 a, ByteVector2 b)
    {
        return new ByteVector2((byte)(a.X - b.X), (byte)(a.Y - b.Y));
    }

    // Multiplication operator with scalar
    public static ByteVector2 operator *(ByteVector2 v, int scalar)
    {
        return new ByteVector2((byte)(v.X * scalar), (byte)(v.Y * scalar));
    }

    // Division operator with scalar
    public static ByteVector2 operator /(ByteVector2 v, int scalar)
    {
        return new ByteVector2((byte)(v.X / scalar), (byte)(v.Y / scalar));
    }

    // Override ToString for better string representation
    public override string ToString()
    {
        return $"({X}, {Y})";
    }
}

public abstract class WorldIndex : VaultModel
{
    public override string SysId { get; set; }
    public override string GroupId { get; set; }
    public Vector2 Position { get; set; }
    public Vector2 Size { get; set; }
    public Vector2 Gravity { get; set; }
    public int Index { get; set; }
    public override DateTime Timestamp { get; set; } = DateTime.UtcNow;
    public override string Type { get; set; } = "WorldIndex";

    public WorldIndex(int index, Vector2 size, Vector2? gravity = null)
    {
        SysId = Guid.NewGuid().ToString();
        GroupId = "";
        Index = index;
        Size = size;
        Gravity = gravity ?? new Vector2(0, 9.81f);
    }

    public int Width => (int)Size.X;
    public int Height => (int)Size.Y;
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

                var partition = new WorldPartition(
                    id: Guid.NewGuid().ToString(),
                    index: new IntVector2(col, row),
                    position: new IntVector2(x, y),
                    size: new IntVector2(width, height)
                );

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
    public IntVector2 Position { get; set; }

    public float Rotation { get; set; }
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

    public virtual void Add(WorldObjectTypeKey type, ObjectMetadata obj)
    {
        string key = GetKey((int)(obj.Position.X / CellSize), (int)(obj.Position.Y / CellSize));

        if (!Grid.TryGetValue(key, out var list))
            Grid[key] = list = new HashSet<string>();

        list.Add(obj.InstanceId);
        InstanceMap[obj.InstanceId] = obj;

        var typeKey = type.Value;
        if (!TypeMap.TryGetValue(typeKey, out var typeDict))
            TypeMap[typeKey] = typeDict = new HashSet<string>();

        typeDict.Add(obj.InstanceId);
    }

    public virtual ObjectMetadata? Remove(WorldObjectTypeKey type, string instanceId)
    {
        if (!InstanceMap.TryGetValue(instanceId, out var obj))
            return null;

        string key = GetKey((int)(obj.Position.X / CellSize), (int)(obj.Position.Y / CellSize));
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

    public virtual IEnumerable<ObjectMetadata> Query(WorldObjectTypeKey type, int x, int y, float radius, string roomId)
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

    public virtual Dictionary<string, ObjectMetadata> GetByType(WorldObjectTypeKey type)
    {
        return (TypeMap.TryGetValue(type.Value, out var map) ? map : new()).ToDictionary(x => x, x => InstanceMap[x]);
    }

    public virtual HashSet<ObjectMetadata> GetAllByType(WorldObjectTypeKey type)
    {
        return GetByType(type).Values.ToHashSet();
    }
}

public class WorldPartition : StoredModel
{
    private readonly SpatialGridIndex _spatialIndex = new(cellSize: 16);
    public override string SysId { get; set; } = Guid.NewGuid().ToString();

    public IntVector2 Index { get; set; }
    public IntVector2 Position { get; set; }
    public IntVector2 Size { get; set; }
    public IntVector2 Epicenter { get; set; }
    public override string Type { get; set; } = "WorldPartition";

    public WorldPartition(
        string id,
        IntVector2 index, IntVector2 position, IntVector2 size)
    {
        SysId = id;
        Index = index;
        Position = position;
        Size = size;
        Epicenter = position + size / 2;
    }

    public virtual void AddObject(WorldObjectTypeKey objectType, ObjectMetadata objectMetadata)
    {
        _spatialIndex.Add(objectType, objectMetadata);
    }

    public virtual ObjectMetadata? DestroyObject(WorldObjectTypeKey objectType, string id)
    {
        return _spatialIndex.Remove(objectType, id);
    }

    public virtual IEnumerable<ObjectMetadata> GetObjectsByTypeInRadius(WorldObjectTypeKey objectType, int x, int y, float radius, string roomId)
    {
        return _spatialIndex.Query(objectType, x, y, radius, roomId);
    }

    public virtual HashSet<ObjectMetadata> GetObjectsByType(WorldObjectTypeKey objectType) =>
        _spatialIndex.GetAllByType(objectType);

    public virtual HashSet<ObjectMetadata> GetObjectsByTypeInRoom(WorldObjectTypeKey objectType, string roomId) =>
        _spatialIndex.GetAllByType(objectType).Where(x => x.RoomId == roomId).ToHashSet();
}

public abstract class PlayerEntity : VaultModel, ISynchronizedEntity
{
#pragma warning disable CS8618 // Non-nullable field is uninitialized. Consider calling the parameterless constructor.

    [Key(0)]
    [JsonPropertyName("id")]
    [VaultColumn]

    public override string SysId { get; set; }

    [Key(1)]
    [Synced(0, SyncAlways: true)]
    [JsonPropertyName("connectionId")]
    [VaultColumn]
    public string ConnectionId { get; set; }

    [Key(2)]
    [Synced(1, SyncAlways: true)]
    [JsonPropertyName("name")]
    [VaultColumn]
    public string Name { get; set; }

    [Key(3)]
    [Synced(2, SyncAlways: true)]
    [JsonPropertyName("type")]
    [VaultColumn]
    public override string Type { get; set; }

    [Key(4)]
    [Synced(3)]
    [JsonPropertyName("level")]
    [VaultColumn]
    public int Level { get; set; }

    [Key(5)]
    [Synced(4)]
    [JsonPropertyName("position")]
    [VaultColumn]
    public float[] Position { get; set; }

    [Key(6)]
    [Synced(5)]
    [JsonPropertyName("rotation")]
    [VaultColumn]
    public float Rotation { get; set; }

    [Key(7)]
    [Synced(6)]
    [JsonPropertyName("currentSpeed")]
    [VaultColumn]
    public float CurrentSpeed { get; set; }

    [Key(8)]
    [JsonPropertyName("rotationSpeed")]
    [VaultColumn]
    [Synced(7)]
    public float RotationSpeed { get; set; }

    [Key(9)]
    [JsonPropertyName("maxSpeed")]
    [Synced(5)]
    [VaultColumn]
    public float MaxSpeed { get; set; }

    [Key(10)]
    [JsonPropertyName("acceleration")]
    [Synced(8)]
    [VaultColumn]
    public float Acceleration { get; set; }

    [Key(11)]
    [JsonPropertyName("deceleration")]
    [Synced(9)]
    [VaultColumn]
    public float Deceleration { get; set; }

    [Key(12)]
    [JsonPropertyName("maxDeceleration")]
    [Synced(10)]
    [VaultColumn]
    public float MaxDeceleration { get; set; }

    [Key(13)]
    [JsonPropertyName("maxAcceleration")]
    [Synced(11)]
    [VaultColumn]
    public float MaxAcceleration { get; set; }

    [Key(14)]
    [JsonPropertyName("worldIndex")]
    [VaultColumn]
    public int WorldIndex { get; set; }

    [Key(15)]
    [JsonPropertyName("moving")]
    [VaultColumn]
    public bool Moving { get; set; }

    [Key(16)]
    [VaultColumn]
    public override DateTime Timestamp { get; set; } = DateTime.UtcNow;

    [Key(17)]
    [VaultColumn]
    public Vector2 Size { get; set; }

    [JsonIgnore]
    [IgnoreMember]
    [VaultIgnored]
    public Body? PhysxBody { get; private set; }

    protected virtual void InitDefaults()
    {
        Type = GetType().Name;
        SysId = Guid.NewGuid().ToString();
        ConnectionId = "";
        Name = "Player";
        Level = 1;
        Position = [0, 0];
        Rotation = 0;
        CurrentSpeed = 0;
        RotationSpeed = 0;
        MaxSpeed = 0;
        Acceleration = 0;
        Deceleration = 0;
        MaxDeceleration = 0;
        MaxAcceleration = 0;
        Size = new Vector2(1, 1);
    }

    public PlayerEntity()

    {
        InitDefaults();
    }

    public PlayerEntity(string id)
    {
        InitDefaults();
        SysId = id;
    }

    public void AttachBody(Body body) => PhysxBody = body;

    public virtual void DetachBody()
    {
        PhysxBody?.World.DestroyBody(PhysxBody);
        PhysxBody = null;
    }

    public virtual PlayerEntity Update()
    {
        var position = PhysxBody?.GetPosition();
        if (PhysxBody != null && (Position[0] != position?.X || Position[1] != position?.Y))
        {
            Position[0] = PhysxBody.GetPosition().X;
            Position[1] = PhysxBody.GetPosition().Y;
            Rotation = PhysxBody.GetAngle();
        }

        return this;
    }

    public virtual Body CalculatePhysxBody(World world)
    {
        if (PhysxBody != null) return PhysxBody;

        // Define the body
        var bodyDef = new BodyDef
        {
            BodyType = BodyType.DynamicBody,
            Position = new Vector2(Position[0], Position[1]),
            Angle = Rotation,
            FixedRotation = true,
            LinearDamping = 1f
        };

        // Create the body
        var body = world.CreateBody(bodyDef);

        // Define the shape
        var shape = new PolygonShape();
        shape.SetAsBox(Size.X * 0.5f, Size.Y * 0.5f); // Box2D uses half-widths

        // Define the fixture
        var fixtureDef = new FixtureDef
        {
            Shape = shape,
            Density = 1f,
            Friction = 0.2f
        };

        // Attach the shape to the body
        body.CreateFixture(fixtureDef);
        AttachBody(body);
        return body;
    }

}

public abstract class Vehicle : PlayerEntity
{
    [VaultColumn]
    [Synced(0)]
    [JsonPropertyName("fuel")]
    public float Fuel { get; set; }

    [VaultColumn]
    [Synced(1)]
    [JsonPropertyName("turboFuel")]
    public float TurboFuel { get; set; }

    [VaultColumn]
    [Synced(2)]
    [JsonPropertyName("maxTurboFuel")]
    public float MaxTurboFuel { get; set; }

    [VaultColumn]
    [Synced(3)]
    [JsonPropertyName("maxTurboSpeed")]
    public float MaxTurboSpeed { get; set; }

    [VaultColumn]
    [Synced(4)]
    [JsonPropertyName("toggleTurbo")]
    public bool ToggleTurbo { get; set; }

    [VaultColumn]
    [Synced(5)]
    [JsonPropertyName("engineQuality")]
    public float EngineQuality { get; set; }

    public Vehicle() { }
    public Vehicle(
    string id,
    int level,
    float[] position,
    float rotation,
    float currentSpeed,
    float maxSpeed,
    float acceleration,
    float maxAcceleration,
    float deceleration,
    float maxDeceleration,
    float rotationSpeed,
    float turboFuel,
    float maxTurboFuel,
    float maxTurboSpeed,
    bool toggleTurbo,
    float engineQuality
)
    {
        SysId = id;
        Level = level;
        Position = position;
        Rotation = rotation;
        CurrentSpeed = currentSpeed;
        MaxSpeed = maxSpeed;
        Acceleration = acceleration;
        MaxAcceleration = maxAcceleration;
        Deceleration = deceleration;
        MaxDeceleration = maxDeceleration;
        RotationSpeed = rotationSpeed;
        TurboFuel = turboFuel;
        MaxTurboFuel = maxTurboFuel;
        MaxTurboSpeed = maxTurboSpeed;
        ToggleTurbo = toggleTurbo;
        EngineQuality = engineQuality;
    }


    public void UpdateSpeed()
    {
        if (CurrentSpeed == 0) return;

        if (CurrentSpeed > 0)
        {
            CurrentSpeed -= Deceleration;
            if (CurrentSpeed < 0)
            {
                CurrentSpeed = 0;
            }
        }
    }
}

public class Spaceship : Vehicle
{
    [VaultColumn]
    [Synced(0)]
    [JsonPropertyName("shootSpeed")]
    public float ShootSpeed { get; set; }

    protected Spaceship()
    {
    }

    protected Spaceship(
    string id,
    int level,
    float[] position,
    float rotation,
    float currentSpeed,
    float maxSpeed,
    float acceleration,
    float maxAcceleration,
    float deceleration,
    float maxDeceleration,
    float rotationSpeed,
    float turboFuel,
    float maxTurboFuel,
    float maxTurboSpeed,
    bool toggleTurbo,
    float engineQuality,
    float shootSpeed
) : base(id, level, position, rotation, currentSpeed, maxSpeed, acceleration, maxAcceleration, deceleration, maxDeceleration, rotationSpeed, turboFuel, maxTurboFuel, maxTurboSpeed, toggleTurbo, engineQuality)
    {
        ShootSpeed = shootSpeed;
    }

}

public abstract class Car : Vehicle
{
    protected Car()
    {
    }

    protected Car(
    string id,
    int level,
    float[] position,
    float rotation,
    float currentSpeed,
    float maxSpeed,
    float acceleration,
    float maxAcceleration,
    float deceleration,
    float maxDeceleration,
    float rotationSpeed,
    float turboFuel,
    float maxTurboFuel,
    float maxTurboSpeed,
    bool toggleTurbo,
    float engineQuality
) : base(id, level, position, rotation, currentSpeed, maxSpeed, acceleration, maxAcceleration, deceleration, maxDeceleration, rotationSpeed, turboFuel, maxTurboFuel, maxTurboSpeed, toggleTurbo, engineQuality)
    {
    }

}