using Altruist.Networking;
using Newtonsoft.Json;
using Altruist.UORM;
using MessagePack;

namespace Altruist;

[MessagePackObject]
public abstract class PlayerEntity : VaultModel, ISynchronizedEntity
{
    [Key(0)]
    [JsonProperty("id")]
    [VaultColumn]

    public override string GenId { get; set; }

    [Key(1)]
    [Synced(0, SyncAlways: true)]
    [JsonProperty("connectionId")]
    [VaultColumn]
    public string ConnectionId { get; set; }

    [Key(2)]
    [Synced(1, SyncAlways: true)]
    [JsonProperty("name")]
    [VaultColumn]
    public string Name { get; set; }

    [Key(3)]
    [Synced(2, SyncAlways: true)]
    [JsonProperty("type")]
    [VaultColumn]
    public override string Type { get; set; }

    [Key(4)]
    [Synced(3)]
    [JsonProperty("level")]
    [VaultColumn]
    public int Level { get; set; }

    [Key(5)]
    [Synced(4)]
    [JsonProperty("position")]
    [VaultColumn]
    public float[] Position { get; set; }

    [Key(6)]
    [Synced(5)]
    [JsonProperty("rotation")]
    [VaultColumn]
    public float Rotation { get; set; }

    [Key(7)]
    [Synced(6)]
    [JsonProperty("currentSpeed")]
    [VaultColumn]
    public float CurrentSpeed { get; set; }

    [Key(8)]
    [JsonProperty("rotationSpeed")]
    [VaultColumn]
    [Synced(7)]
    public float RotationSpeed { get; set; }

    [Key(9)]
    [JsonProperty("maxSpeed")]
    [Synced(5)]
    [VaultColumn]
    public float MaxSpeed { get; set; }

    [Key(10)]
    [JsonProperty("acceleration")]
    [Synced(8)]
    [VaultColumn]
    public float Acceleration { get; set; }

    [Key(11)]
    [JsonProperty("deceleration")]
    [Synced(9)]
    [VaultColumn]
    public float Deceleration { get; set; }

    [Key(12)]
    [JsonProperty("maxDeceleration")]
    [Synced(10)]
    [VaultColumn]
    public float MaxDeceleration { get; set; }

    [Key(13)]
    [JsonProperty("maxAcceleration")]
    [Synced(11)]
    [VaultColumn]
    public float MaxAcceleration { get; set; }

    [Key(14)]
    [JsonProperty("worldIndex")]
    [VaultColumn]
    public int WorldIndex { get; set; }

    [Key(15)]
    [VaultColumn]
    public override DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public PlayerEntity()
    {
        Type = GetType().Name;
        GenId = Guid.NewGuid().ToString();
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
    }

    public PlayerEntity(string id)
    {
        Type = GetType().Name;
        GenId = id;
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

public abstract class Vehicle : PlayerEntity
{
    [VaultColumn]
    public float Fuel { get; set; }

    [VaultColumn]
    public float TurboFuel { get; set; }

    [VaultColumn]
    public float MaxTurboFuel { get; set; }

    [VaultColumn]
    public float EngineQuality { get; set; }

    public Vehicle() { }
    public Vehicle(string id, int level, float[] position, float rotation, float currentSpeed, float maxSpeed, float acceleration, float maxAcceleration, float deceleration, float maxDeceleration, float rotationSpeed, float turboFuel, float maxTurboFuel, float engineQuality)
    {
        GenId = id;
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

public class Spaceship : Vehicle
{
    protected Spaceship()
    {
    }

    protected Spaceship(string id, int level, float[] position, float rotation, float currentSpeed, float maxSpeed, float acceleration, float maxAcceleration, float deceleration, float maxDeceleration, float rotationSpeed, float turboFuel, float maxTurboFuel, float engineQuality) : base(id, level, position, rotation, currentSpeed, maxSpeed, acceleration, maxAcceleration, deceleration, maxDeceleration, rotationSpeed, turboFuel, maxTurboFuel, engineQuality)
    {
    }
}

public abstract class Car : Vehicle
{
    protected Car()
    {
    }

    protected Car(string id, int level, float[] position, float rotation, float currentSpeed, float maxSpeed, float acceleration, float maxAcceleration, float deceleration, float maxDeceleration, float rotationSpeed, float turboFuel, float maxTurboFuel, float engineQuality) : base(id, level, position, rotation, currentSpeed, maxSpeed, acceleration, maxAcceleration, deceleration, maxDeceleration, rotationSpeed, turboFuel, maxTurboFuel, engineQuality)
    {
    }
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

