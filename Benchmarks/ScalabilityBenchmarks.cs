using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.Combat;
using Altruist.Gaming.ThreeD;
using Altruist.Networking;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Altruist.Benchmarks;

/// <summary>
/// Full-tick benchmarks: one complete framework tick for a world of
/// <see cref="PlayerCount"/> observers and <see cref="NpcCount"/> AI entities.
///
/// <para><b>FullTick</b> runs, in order, every stage the framework executes per tick:</para>
/// <list type="number">
///   <item>Movement — every entity moves 1 unit (direction alternates each tick) so sync
///         sees real position deltas. Not framework work, but O(n) and negligible.</item>
///   <item>AI — one <see cref="AIStateMachine"/> per NPC, <c>Update(ctx, 1/30 s)</c>.</item>
///   <item>Sync — <see cref="Synchronization.GetSyncChanges"/> for every [Synchronized]
///         entity, keyed by InstanceId, plus the observer fan-out lookup
///         (<see cref="VisibilityTracker3D.GetObserversOf"/>) exactly like
///         EntitySyncService (serialization and socket send excluded).</item>
///   <item>Visibility — <see cref="VisibilityTracker3D.Tick"/> (parallel + stagger).</item>
///   <item>Collision — <see cref="SpatialCollisionDispatcher.Tick"/> (broadphase).</item>
///   <item>Combat — every player performs one single-target attack, and one AoE
///         sphere sweep (r=500) is issued per 10 players.</item>
/// </list>
///
/// <para>The <c>*Only</c> methods run a single stage with the same inputs so the
/// per-system breakdown can be read off directly. FullTick at
/// PlayerCount=50 / NpcCount=500 is the "total framework overhead per tick" figure;
/// the slope of FullTick over PlayerCount is the marginal per-player cost used for
/// the CCU estimate (see BENCHMARK_RESULTS.md, "Reproducing the numbers").</para>
/// </summary>
[MemoryDiagnoser]
public class ScalabilityBenchmarks
{
    private const float Dt = 1f / 30f;

    [Synchronized]
    public class ScalePlayer : WorldObject3D, ISynchronizedEntity, ICombatEntity
    {
        [Synced(0, SyncAlways: true)] public string Name { get; set; } = "";
        [Synced(1)] public int PosX => (int)Transform.Position.X;
        [Synced(2)] public int PosY => (int)Transform.Position.Y;
        [Synced(3)] public int Hp { get => Health; set => Health = value; }

        public int Health { get; set; } = int.MaxValue / 2;
        public int MaxHealth => int.MaxValue / 2;
        public bool IsDead => Health <= 0;
        float ICombatEntity.X => Transform.Position.X;
        float ICombatEntity.Y => Transform.Position.Y;
        float ICombatEntity.Z => Transform.Position.Z;
        public int GetAttackPower() => 11;
        public int GetDefensePower() => 10;

        public ScalePlayer(float x, float y, string clientId, uint vid)
            : base(Transform3D.From(new Vector3(x, y, 0), Quaternion.Identity, Vector3.One))
        {
            // Base ClientId: non-empty marks this object as a visibility observer.
            ClientId = clientId;
            Name = clientId;
            VirtualId = vid;
            ColliderDescriptors = [PhysxCollider3D.CreateSphere(50, isTrigger: true)];
        }

        public override void Step(float dt, IGameWorldManager3D world) { }
    }

    [Synchronized]
    public class ScaleNpc : WorldObject3D, ISynchronizedEntity, IAIBehaviorEntity, ICombatEntity
    {
        public string AIBehaviorName => "bench_scale";
        public IAIContext AIContext { get; set; } = null!;
        public AIStateMachine Fsm { get; set; } = null!;

        [Synced(0)] public int PosX => (int)Transform.Position.X;
        [Synced(1)] public int PosY => (int)Transform.Position.Y;
        [Synced(2)] public int Hp { get => Health; set => Health = value; }

        public int Health { get; set; } = int.MaxValue / 2;
        public int MaxHealth => int.MaxValue / 2;
        public bool IsDead => Health <= 0;
        float ICombatEntity.X => Transform.Position.X;
        float ICombatEntity.Y => Transform.Position.Y;
        float ICombatEntity.Z => Transform.Position.Z;
        public int GetAttackPower() => 11;
        public int GetDefensePower() => 10;

        public ScaleNpc(float x, float y, uint vid)
            : base(Transform3D.From(new Vector3(x, y, 0), Quaternion.Identity, Vector3.One))
        {
            // NPCs keep an empty ClientId (not observers), like real AI entities.
            VirtualId = vid;
            ColliderDescriptors = [PhysxCollider3D.CreateSphere(50, isTrigger: true)];
        }

        public override void Step(float dt, IGameWorldManager3D world) { }
    }

    public class ScaleAIContext : IAIContext
    {
        public ITypelessWorldObject Entity { get; set; } = null!;
        public float TimeInState { get; set; }
        public float StateDuration { get; set; }
        public string CurrentStateTag { get; set; } = "";
        public IReadOnlyDictionary<string, StateWindow>? ActiveWindows { get; set; }
        public object? CurrentStateData { get; set; }
        public StateMotionProfile? CurrentStateMotion { get; set; }
        public float PreviousProgress { get; set; }
    }

    /// <summary>Two-state patrol behaviour: Idle ⇄ Wander every 2 s (≈ every 60 ticks at 30 Hz).</summary>
    [AIBehavior("bench_scale")]
    public class ScaleBehavior
    {
        [AIState("Idle", Initial = true)]
        public string? Idle(ScaleAIContext ctx, float dt) => ctx.TimeInState > 2f ? "Wander" : null;

        [AIState("Wander")]
        public string? Wander(ScaleAIContext ctx, float dt) => ctx.TimeInState > 2f ? "Idle" : null;
    }

    private VisibilityTracker3D _visibility = null!;
    private SpatialCollisionDispatcher _collision = null!;
    private CombatService _combat = null!;
    private IGameWorldManager3D _world = null!;
    private WorldSnapshot[] _snapshots = null!;
    private List<ITypelessWorldObject> _allObjects = null!;
    private ScalePlayer[] _players = null!;
    private ScaleNpc[] _npcs = null!;
    private long _tick;

    [Params(50, 200, 500, 1000)]
    public int PlayerCount { get; set; }

    [Params(500, 2000)]
    public int NpcCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        BenchmarkHelpers.DiscoverAIBehaviors(typeof(ScaleBehavior).Assembly);

        var rng = new Random(42);
        _allObjects = new List<ITypelessWorldObject>(PlayerCount + NpcCount);
        _players = new ScalePlayer[PlayerCount];
        _npcs = new ScaleNpc[NpcCount];

        // Players clustered in a 2000-unit area (typical town/zone)
        for (int i = 0; i < PlayerCount; i++)
        {
            _players[i] = new ScalePlayer(
                5000 + rng.Next(-1000, 1000),
                5000 + rng.Next(-1000, 1000),
                $"player_{i}",
                (uint)(i + 1));
            _allObjects.Add(_players[i]);
        }

        // NPCs spread across a 10000x10000 map
        for (int i = 0; i < NpcCount; i++)
        {
            var npc = new ScaleNpc(rng.Next(0, 10000), rng.Next(0, 10000), (uint)(PlayerCount + i + 1));
            npc.AIContext = new ScaleAIContext { Entity = npc };
            npc.Fsm = AIBehaviorDiscovery.CreateStateMachine("bench_scale")
                ?? throw new InvalidOperationException("AI behavior 'bench_scale' not discovered");
            _npcs[i] = npc;
            _allObjects.Add(npc);
        }

        var lookup = _allObjects.ToDictionary(o => o.InstanceId, o => o);
        _snapshots = [new WorldSnapshot(0, _allObjects, lookup)];

        // One world mock serves visibility (Index), collision + combat (cached snapshot).
        var list3d = _allObjects.Cast<IWorldObject3D>().ToList();
        var lookup3d = list3d.ToDictionary(o => o.InstanceId, o => o);
        var world = new Mock<IGameWorldManager3D>();
        var index = new Mock<IWorldIndex3D>();
        index.Setup(i => i.Index).Returns(0);
        world.Setup(w => w.Index).Returns(index.Object);
        world.Setup(w => w.GetCachedSnapshot())
            .Returns((list3d as IReadOnlyList<IWorldObject3D>,
                      lookup3d as IReadOnlyDictionary<string, IWorldObject3D>));
        world.Setup(w => w.SnapshotVersion).Returns(1);
        _world = world.Object;

        var organizer = new Mock<IGameWorldOrganizer3D>();
        organizer.Setup(o => o.GetWorld(0)).Returns(_world);

        _visibility = new VisibilityTracker3D(5000f);
        _visibility.SetOrganizer(organizer.Object);
        _collision = new SpatialCollisionDispatcher(NullLoggerFactory.Instance);
        _combat = new CombatService(NullLoggerFactory.Instance, new DefaultDamageCalculator(), organizer.Object);

        // Warm-up: populate visibility sets (both stagger groups), sync baselines, overlaps.
        _visibility.Tick(_snapshots);
        _visibility.Tick(_snapshots);
        _collision.Tick(_world);
        SyncStage();
    }

    [Benchmark(Description = "Full tick: AI + sync + visibility + collision + combat")]
    public void FullTick()
    {
        MoveStage();
        AIStage();
        SyncStage();
        _visibility.Tick(_snapshots);
        _collision.Tick(_world);
        CombatStage();
    }

    [Benchmark(Description = "Sync only (move + delta detect all entities)")]
    public void SyncOnly()
    {
        MoveStage();
        SyncStage();
    }

    [Benchmark(Description = "AI only (all NPCs)")]
    public void AIOnly() => AIStage();

    [Benchmark(Description = "Visibility only")]
    public void VisibilityOnly() => _visibility.Tick(_snapshots);

    [Benchmark(Description = "Collision only")]
    public void CollisionOnly() => _collision.Tick(_world);

    [Benchmark(Description = "Combat only")]
    public void CombatOnly() => CombatStage();

    private void MoveStage()
    {
        float dx = (_tick & 1) == 0 ? 1f : -1f;
        for (int i = 0; i < _allObjects.Count; i++)
        {
            var obj = (IWorldObject3D)_allObjects[i];
            var p = obj.Transform.Position;
            obj.Transform = obj.Transform.WithPosition(new Position3D(p.X + dx, p.Y, p.Z));
        }
    }

    private void AIStage()
    {
        for (int i = 0; i < _npcs.Length; i++)
            _npcs[i].Fsm.Update(_npcs[i].AIContext, Dt);
    }

    private void SyncStage()
    {
        long tick = ++_tick;
        for (int i = 0; i < _allObjects.Count; i++)
        {
            var obj = _allObjects[i];
            if (obj is not ISynchronizedEntity sync) continue;
            using var changes = Synchronization.GetSyncChanges(sync, obj.InstanceId, tick);
            if (!changes.HasChanges) continue;
            foreach (var _ in _visibility.GetObserversOf(obj.InstanceId)) { }
        }
    }

    private void CombatStage()
    {
        for (int i = 0; i < _players.Length; i++)
            _combat.Attack(_players[i], _npcs[i % _npcs.Length]);

        for (int i = 0; i < _players.Length; i += 10)
        {
            var p = _players[i].Transform.Position;
            _combat.Sweep(_players[i], SweepQuery3D.Sphere(p.X, p.Y, p.Z, 500), damage: 1);
        }
    }
}
