using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.Combat;
using Altruist.Gaming.ThreeD;
using Altruist.Networking;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace CombatArena3D;

/// <summary>
/// Player entity — [Synchronized] broadcasts position/HP to nearby clients.
/// </summary>
[Synchronized]
[WorldObject("player")]
public class ArenaPlayer : WorldObject3D, ICombatEntity, ISynchronizedEntity
{
    public string ClientId { get; set; } = "";

    [Synced(0, SyncAlways: true)]
    public string Name { get; set; } = "";

    [Synced(1)]
    public int PosX => (int)Transform.Position.X;

    [Synced(2)]
    public int PosY => (int)Transform.Position.Y;

    [Synced(3)]
    public int PosZ => (int)Transform.Position.Z;

    [Synced(4)]
    public int Hp { get; set; } = 200;

    [Synced(5)]
    public int MaxHp { get; set; } = 200;

    [Synced(6)]
    public bool IsDead { get; set; }

    // ICombatEntity
    int ICombatEntity.Health { get => Hp; set => Hp = value; }
    int ICombatEntity.MaxHealth => MaxHp;
    float ICombatEntity.X => Transform.Position.X;
    float ICombatEntity.Y => Transform.Position.Y;
    float ICombatEntity.Z => Transform.Position.Z;
    public int GetAttackPower() => 40;
    public int GetDefensePower() => 15;

    public ArenaPlayer(uint vid, string name, float x, float y, float z, string clientId)
        : base(Transform3D.From(new Vector3(x, y, z), Quaternion.Identity, Vector3.One))
    {
        VirtualId = vid;
        Name = name;
        ClientId = clientId;
    }

    public override void Step(float dt, IGameWorldManager3D world) { }
}

/// <summary>
/// Monster entity — AI-controlled, has aggro sphere collider.
/// [Synchronized] auto-syncs to nearby players via visibility tracker.
/// </summary>
[Synchronized]
[WorldObject("monster")]
public class ArenaMonster : WorldObject3D, ICombatEntity, IAIBehaviorEntity, ISynchronizedEntity
{
    public string AIBehaviorName => "arena_monster";
    public IAIContext AIContext { get; set; } = null!;
    public string ClientId { get; set; } = "";

    [Synced(0, SyncAlways: true)]
    public string Name { get; set; } = "";

    [Synced(1)]
    public int PosX => (int)Transform.Position.X;

    [Synced(2)]
    public int PosY => (int)Transform.Position.Y;

    [Synced(3)]
    public int PosZ => (int)Transform.Position.Z;

    [Synced(4)]
    public int Hp { get; set; } = 100;

    [Synced(5)]
    public bool IsDead { get; set; }

    // ICombatEntity
    int ICombatEntity.Health { get => Hp; set => Hp = value; }
    int ICombatEntity.MaxHealth => 100;
    float ICombatEntity.X => Transform.Position.X;
    float ICombatEntity.Y => Transform.Position.Y;
    float ICombatEntity.Z => Transform.Position.Z;
    public int GetAttackPower() => 20;
    public int GetDefensePower() => 8;

    public ArenaMonster(uint vid, string name, float x, float y, float z)
        : base(Transform3D.From(new Vector3(x, y, z), Quaternion.Identity, Vector3.One))
    {
        VirtualId = vid;
        Name = name;
        ClientId = $"mob_{vid}";

        // Aggro sphere — monsters detect players within 2000 units
        ColliderDescriptors = [PhysxCollider3D.CreateSphere(2000, isTrigger: true)];
    }

    public override void Step(float dt, IGameWorldManager3D world) { }
}
