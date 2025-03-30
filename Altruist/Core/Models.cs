using Redis.OM.Modeling;
using Altruist.Networking;
using Newtonsoft.Json;
using Altruist.Database;
using MessagePack;

namespace Altruist;

[Document(StorageType = StorageType.Json, IndexName = "Players", Prefixes = new[] { "player" })]
[Table("player")]
[PrimaryKey(keys: [nameof(Id), nameof(Name)])]
[MessagePackObject]
public class PlayerEntity : ISynchronizedEntity, IVaultModel
{
    [Key(0)]
    [Indexed]
    [JsonProperty("id")]
    [Column]
    [RedisIdField]
    public string Id { get; set; }

    [Key(1)]
    [Indexed]
    [Synced(0, SyncAlways: true)]
    [JsonProperty("connectionId")]
    [Column]
    public string ConnectionId { get; set; }

    [Key(2)]
    [Indexed]
    [Synced(1, SyncAlways: true)]
    [JsonProperty("name")]
    [Column]
    public string Name { get; set; }

    [Key(3)]
    [Indexed]
    [Synced(2, SyncAlways: true)]
    [JsonProperty("type")]
    [Column]
    public string Type { get; set; }

    [Key(4)]
    [Synced(3)]
    [JsonProperty("level")]
    [Column]
    public int Level { get; set; }

    [Key(5)]
    [Synced(4)]
    [JsonProperty("position")]
    [Column]
    public float[] Position { get; set; }

    [Key(6)]
    [Synced(5)]
    [JsonProperty("rotation")]
    [Column]
    public float Rotation { get; set; }

    [Key(7)]
    [Synced(6)]
    [JsonProperty("currentSpeed")]
    [Column]
    public float CurrentSpeed { get; set; }

    [Key(8)]
    [JsonProperty("rotationSpeed")]
    [Column]
    [Synced(7)]
    public float RotationSpeed { get; set; }

    [Key(9)]
    [JsonProperty("maxSpeed")]
    [Synced(5)]
    [Column]
    public float MaxSpeed { get; set; }

    [Key(10)]
    [JsonProperty("acceleration")]
    [Synced(8)]
    [Column]
    public float Acceleration { get; set; }

    [Key(11)]
    [JsonProperty("deceleration")]
    [Synced(9)]
    [Column]
    public float Deceleration { get; set; }

    [Key(12)]
    [JsonProperty("maxDeceleration")]
    [Synced(10)]
    [Column]
    public float MaxDeceleration { get; set; }

    [Key(13)]
    [JsonProperty("maxAcceleration")]
    [Synced(11)]
    [Column]
    public float MaxAcceleration { get; set; }

    [Key(14)]
    [JsonIgnore]
    [Ignore]
    [IgnoreMember]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public PlayerEntity()
    {
        Type = GetType().Name;
        Id = Guid.NewGuid().ToString();
        ConnectionId = Guid.NewGuid().ToString();
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
    }

    public PlayerEntity(string id)
    {
        Type = GetType().Name;
        Id = id;
        ConnectionId = Guid.NewGuid().ToString();
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
    }
}


[Document(StorageType = StorageType.Json, IndexName = "players", Prefixes = new[] { "player" })]
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
    public Vehicle(string id, int level, float[] position, float rotation, float currentSpeed, float maxSpeed, float acceleration, float maxAcceleration, float deceleration, float maxDeceleration, float rotationSpeed, float turboFuel, float maxTurboFuel, float engineQuality)
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

[Document(StorageType = StorageType.Json, IndexName = "players", Prefixes = new[] { "player" })]
[Table("Spaceship")]
public class Spaceship : Vehicle
{
}

[Document(StorageType = StorageType.Json, IndexName = "players", Prefixes = new[] { "player" })]
[Table("Car")]
public class Car : Vehicle
{
}

// [Document(StorageType = StorageType.Json, IndexName = "Players", Prefixes = new[] { "player" })]
// [Table("player", StoreHistory: true)]
// [PrimaryKey(keys: [nameof(Id), nameof(Name)])]
// public class Player : IVaultModel
// {
//     [RedisIdField]
//     [Indexed]
//     [Column]
//     public string Id { get; set; }

//     [Indexed]
//     [Column]
//     public required string Name { get; set; }

//     [JsonIgnore]
//     [Ignore]
//     [IgnoreMember]
//     public DateTime Timestamp { get; set; } = DateTime.UtcNow;

//     public Player()
//     {
//         Id = Guid.NewGuid().ToString();
//     }

//     public Player(string entityId, string name) => (Id, Name) = (entityId, name);
// }

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

