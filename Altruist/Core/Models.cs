using Redis.OM.Modeling;
using Microsoft.Xna.Framework;
using Altruist.Networking;
using Newtonsoft.Json;
using Altruist.Database;
using MessagePack;

namespace Altruist;


[Document(StorageType = StorageType.Json, IndexName = "Entities", Prefixes = new[] { "entity" })]
[Table("entities")]
[PrimaryKey(keys: [nameof(Id)])]
public class PlayerEntity : ISynchronizedEntity, IVaultModel
{
    [Indexed]
    [JsonProperty("id")]
    [Column]
    public required string Id { get; set; }

    [Indexed]
    [JsonProperty("connectionId")]
    [Column]
    public required string ConnectionId { get; set; }

    [Indexed]
    [JsonProperty("type")]
    [Column]
    public string Type { get; set; }

    [Synced(0)]
    [JsonProperty("level")]
    [Column]
    public int Level { get; set; }

    [Synced(1)]
    [JsonProperty("position")]
    [Column]
    public Vector2 Position { get; set; }

    [Synced(2)]
    [JsonProperty("rotation")]
    [Column]
    public float Rotation { get; set; }

    [Synced(3)]
    [JsonProperty("currentSpeed")]
    [Column]
    public float CurrentSpeed { get; set; }

    [JsonProperty("rotationSpeed")]
    [Column]
    [Synced(4)]
    public float RotationSpeed { get; set; }

    [JsonProperty("maxSpeed")]
    [Synced(5)]
    [Column]
    public float MaxSpeed { get; set; }

    [JsonProperty("acceleration")]
    [Synced(6)]
    [Column]
    public float Acceleration { get; set; }

    [JsonProperty("deceleration")]
    [Synced(7)]
    [Column]
    public float Deceleration { get; set; }

    [JsonProperty("maxDeceleration")]
    [Synced(8)]
    [Column]
    public float MaxDeceleration { get; set; }

    [JsonProperty("maxAcceleration")]
    [Synced(9)]
    [Column]
    public float MaxAcceleration { get; set; }

    [JsonIgnore]
    [Ignore]
    [IgnoreMember]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public PlayerEntity()
    {
        Type = GetType().Name;
    }

    public PlayerEntity(string id)
    {
        Type = GetType().Name;
        Id = id;
    }
}


[Document(StorageType = StorageType.Json, IndexName = "Entities", Prefixes = new[] { "entity" })]
[Table("vehicles")]
public abstract class Vehicle : PlayerEntity
{
    [Column]
    public float Fuel { get; set; }

    [Column]
    public float TurboFuel { get; set; }

    [Column]
    public float MaxTurboFuel { get; set; }

    [Column]
    public float EngineQuality { get; set; }

    public Vehicle() { }
    public Vehicle(string id, int level, Vector2 position, float rotation, float currentSpeed, float maxSpeed, float acceleration, float maxAcceleration, float deceleration, float maxDeceleration, float rotationSpeed, float turboFuel, float maxTurboFuel, float engineQuality)
    {
        Id = id;
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

[Document(StorageType = StorageType.Json, IndexName = "Entities", Prefixes = new[] { "entity" })]
[Table("Spaceship")]
public class Spaceship : Vehicle
{
}

[Document(StorageType = StorageType.Json, IndexName = "Entities", Prefixes = new[] { "entity" })]
[Table("Car")]
public class Car : Vehicle
{
}

[Document(StorageType = StorageType.Json, IndexName = "Players", Prefixes = new[] { "player" })]
[Table("player", StoreHistory: true)]
[PrimaryKey(keys: [nameof(Id), nameof(Name)])]
public class Player : IVaultModel
{
    [RedisIdField]
    [Indexed]
    [Column]
    public string Id { get; set; }

    [Indexed]
    [Column]
    public required string Name { get; set; }

    [JsonIgnore]
    [Ignore]
    [IgnoreMember]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public Player()
    {
        Id = Guid.NewGuid().ToString();
    }

    public Player(string entityId, string name) => (Id, Name) = (entityId, name);
}

public class ServerInfo
{
    public string Name { get; set; }
    public string Protocol { get; set; }
    public string Host { get; set; }
    public int Port { get; set; }

    public ServerInfo(string name, string protocol, string host, int port)
    {
        Name = name;
        Host = host;
        Port = port;
        Protocol = protocol;
    }
}

public abstract class MovementInput
{
    public float RotationSpeed { get; set; } = 0f;
    public bool Turbo { get; set; }
}


public class ForwardMovementInput : MovementInput
{
    public bool MoveUp { get; set; }
    public bool RotateLeft { get; set; }
    public bool RotateRight { get; set; }
}


public class EightDirectionMovementInput : MovementInput
{
    public bool MoveUp { get; set; }
    public bool MoveDown { get; set; }
    public bool MoveLeft { get; set; }
    public bool MoveRight { get; set; }
}


public class VehicleMovementInput : EightDirectionMovementInput
{

}


public class SpaceshipMovementInput : ForwardMovementInput
{

}

