using Altruist.Gaming;
using Altruist.Gaming.Combat;
using Altruist.Gaming.TwoD;
using Altruist.Networking;
using Altruist.TwoD.Numerics;

namespace ShooterGame2D;

/// <summary>
/// Player entity — [Synchronized] auto-syncs position and HP to all nearby clients.
/// </summary>
[Synchronized]
[WorldObject("player")]
public class PlayerShip : WorldObject2D, ICombatEntity, ISynchronizedEntity
{
    public string ClientId { get; set; } = "";

    [Synced(0, SyncAlways: true)]
    public string Name { get; set; } = "";

    [Synced(1)]
    public int PosX => Transform.Position.X;

    [Synced(2)]
    public int PosY => Transform.Position.Y;

    [Synced(3)]
    public int Hp { get; set; } = 100;

    [Synced(4)]
    public int MaxHp { get; set; } = 100;

    [Synced(5)]
    public bool IsDead { get; set; }

    // ICombatEntity
    uint ICombatEntity.VirtualId => VirtualId;
    int ICombatEntity.Health { get => Hp; set => Hp = value; }
    int ICombatEntity.MaxHealth => MaxHp;
    float ICombatEntity.X => Transform.Position.X;
    float ICombatEntity.Y => Transform.Position.Y;
    float ICombatEntity.Z => 0;
    public int GetAttackPower() => 25;
    public int GetDefensePower() => 5;

    public uint VirtualId { get; set; }

    public PlayerShip(uint vid, string name, int x, int y, string clientId)
        : base(new Transform2D(Position2D.Of(x, y), Size2D.Of(32, 32), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)))
    {
        VirtualId = vid;
        Name = name;
        ClientId = clientId;
    }

    public override void Step(float dt, IGameWorldManager2D world) { }
}

/// <summary>
/// Enemy drone — AI-controlled, chases nearest player.
/// </summary>
[Synchronized]
[WorldObject("drone")]
public class EnemyDrone : WorldObject2D, ICombatEntity, IAIBehaviorEntity, ISynchronizedEntity
{
    public string AIBehaviorName => "chase_drone";
    public IAIContext AIContext { get; set; } = null!;
    public string ClientId { get; set; } = "";

    [Synced(0)]
    public int PosX => Transform.Position.X;

    [Synced(1)]
    public int PosY => Transform.Position.Y;

    [Synced(2)]
    public int Hp { get; set; } = 50;

    [Synced(3)]
    public bool IsDead { get; set; }

    // ICombatEntity
    uint ICombatEntity.VirtualId => VirtualId;
    int ICombatEntity.Health { get => Hp; set => Hp = value; }
    int ICombatEntity.MaxHealth => 50;
    float ICombatEntity.X => Transform.Position.X;
    float ICombatEntity.Y => Transform.Position.Y;
    float ICombatEntity.Z => 0;
    public int GetAttackPower() => 10;
    public int GetDefensePower() => 2;

    public uint VirtualId { get; set; }

    public EnemyDrone(uint vid, int x, int y)
        : base(new Transform2D(Position2D.Of(x, y), Size2D.Of(24, 24), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)))
    {
        VirtualId = vid;
        ClientId = $"drone_{vid}"; // AI entities use synthetic IDs for sync tracking
    }

    public override void Step(float dt, IGameWorldManager2D world) { }
}
