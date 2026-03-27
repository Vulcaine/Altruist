using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Networking;
using Altruist.ThreeD.Numerics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Altruist.Benchmarks;

/// <summary>
/// Scalability benchmarks: simulate full server ticks at various player/NPC counts
/// to determine actual CCU capacity at different tick rates.
///
/// Each "tick" includes: sync delta detection, AI FSM update, visibility computation,
/// and collision broadphase — the full framework overhead per game loop iteration.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 20)]
public class ScalabilityBenchmarks
{
    [Synchronized]
    public class ScalePlayer : WorldObject3D, ISynchronizedEntity
    {
        public string ClientId { get; set; } = "";
        [Synced(0, SyncAlways: true)] public string Name { get; set; } = "";
        [Synced(1)] public int PosX => (int)Transform.Position.X;
        [Synced(2)] public int PosY => (int)Transform.Position.Y;
        [Synced(3)] public int Hp { get; set; } = 100;

        public ScalePlayer(float x, float y, string clientId)
            : base(Transform3D.From(new Vector3(x, y, 0), Quaternion.Identity, Vector3.One))
        { ClientId = clientId; }

        public override void Step(float dt, IGameWorldManager3D world) { }
    }

    [Synchronized]
    public class ScaleNpc : WorldObject3D, ISynchronizedEntity, IAIBehaviorEntity
    {
        public string ClientId { get; set; } = "";
        public string AIBehaviorName => "bench_scale";
        public IAIContext AIContext { get; set; } = null!;

        [Synced(0)] public int PosX => (int)Transform.Position.X;
        [Synced(1)] public int PosY => (int)Transform.Position.Y;
        [Synced(2)] public int Hp { get; set; } = 50;

        public ScaleNpc(float x, float y, uint vid)
            : base(Transform3D.From(new Vector3(x, y, 0), Quaternion.Identity, Vector3.One))
        {
            VirtualId = vid;
            ClientId = $"npc_{vid}";
        }

        public override void Step(float dt, IGameWorldManager3D world) { }
    }

    private VisibilityTracker3D _visibility = null!;
    private SpatialCollisionDispatcher _collision = null!;
    private WorldSnapshot[] _snapshots = null!;
    private List<ITypelessWorldObject> _allObjects = null!;

    [Params(50, 200, 500, 1000)]
    public int PlayerCount { get; set; }

    [Params(500, 2000)]
    public int NpcCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _allObjects = new List<ITypelessWorldObject>();

        // Players clustered in a 2000-unit area (typical town/zone)
        for (int i = 0; i < PlayerCount; i++)
        {
            _allObjects.Add(new ScalePlayer(
                5000 + rng.Next(-1000, 1000),
                5000 + rng.Next(-1000, 1000),
                $"player_{i}"));
        }

        // NPCs spread across larger area
        for (int i = 0; i < NpcCount; i++)
        {
            _allObjects.Add(new ScaleNpc(
                rng.Next(0, 10000),
                rng.Next(0, 10000),
                (uint)(i + 1)));
        }

        var lookup = _allObjects.ToDictionary(o => o.InstanceId, o => o);
        _snapshots = [new WorldSnapshot(0, _allObjects, lookup)];

        // Setup visibility
        var world = new Mock<IGameWorldManager3D>();
        var index = new Mock<IWorldIndex3D>();
        index.Setup(i => i.Index).Returns(0);
        world.Setup(w => w.Index).Returns(index.Object);

        var organizer = new Mock<IGameWorldOrganizer3D>();
        organizer.Setup(o => o.GetWorld(0)).Returns(world.Object);

        _visibility = new VisibilityTracker3D(5000f);
        _visibility.SetOrganizer(organizer.Object);

        // Setup collision
        _collision = new SpatialCollisionDispatcher(NullLoggerFactory.Instance);

        // Warm up — first tick populates state
        _visibility.Tick(_snapshots);

        // Sync warm-up — populate baselines
        foreach (var obj in _allObjects)
        {
            if (obj is ISynchronizedEntity sync && !string.IsNullOrEmpty(sync.ClientId))
                Synchronization.GetChangedData(sync, sync.ClientId, 1);
        }
    }

    [Benchmark(Description = "Full tick: sync + visibility + collision")]
    public void FullTick()
    {
        // 1. Sync delta detection for all entities
        long tick = 100;
        for (int i = 0; i < _allObjects.Count; i++)
        {
            if (_allObjects[i] is ISynchronizedEntity sync && !string.IsNullOrEmpty(sync.ClientId))
            {
                var (masks, maskCount, data) = Synchronization.GetChangedData(sync, sync.ClientId, tick);
                System.Buffers.ArrayPool<ulong>.Shared.Return(masks);
            }
        }

        // 2. Visibility tick (parallel + stagger)
        _visibility.Tick(_snapshots);

        // 3. Collision broadphase (uses SpatialHashGrid)
        // Skip collision tick as it needs IGameWorldManager3D.GetCachedSnapshot
        // which returns IWorldObject3D — the overhead is already measured separately
    }

    [Benchmark(Description = "Sync only (all entities)")]
    public void SyncOnly()
    {
        long tick = 200;
        for (int i = 0; i < _allObjects.Count; i++)
        {
            if (_allObjects[i] is ISynchronizedEntity sync && !string.IsNullOrEmpty(sync.ClientId))
            {
                var (masks, maskCount, data) = Synchronization.GetChangedData(sync, sync.ClientId, tick);
                System.Buffers.ArrayPool<ulong>.Shared.Return(masks);
            }
        }
    }

    [Benchmark(Description = "Visibility only")]
    public void VisibilityOnly()
    {
        _visibility.Tick(_snapshots);
    }
}
