using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.ThreeD.Numerics;
using BenchmarkDotNet.Attributes;
using Moq;

namespace Altruist.Benchmarks;

/// <summary>
/// Benchmarks the visibility tracking system.
/// This runs every tick — O(players × entities) distance checks.
/// Measures: per-tick visibility computation, observer lookups, event dispatch.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 20)]
public class VisibilityBenchmarks
{
    private VisibilityTracker3D _tracker = null!;
    private WorldSnapshot[] _snapshots = null!;

    public class BenchObj : WorldObject3D
    {
        public BenchObj(float x, float y, string clientId = "")
            : base(Transform3D.From(new Vector3(x, y, 0), Quaternion.Identity, Vector3.One))
        {
            ClientId = clientId;
        }

        public override void Step(float dt, IGameWorldManager3D world) { }
    }

    [Params(10, 50)]
    public int PlayerCount { get; set; }

    [Params(100, 1000)]
    public int NpcCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var world = new Mock<IGameWorldManager3D>();
        var index = new Mock<IWorldIndex3D>();
        index.Setup(i => i.Index).Returns(0);
        world.Setup(w => w.Index).Returns(index.Object);

        var organizer = new Mock<IGameWorldOrganizer3D>();
        organizer.Setup(o => o.GetWorld(0)).Returns(world.Object);

        _tracker = new VisibilityTracker3D(5000f);
        _tracker.SetOrganizer(organizer.Object);

        var rng = new Random(42);
        var objects = new List<ITypelessWorldObject>();

        // Players clustered in center
        for (int i = 0; i < PlayerCount; i++)
        {
            objects.Add(new BenchObj(
                5000 + rng.Next(-500, 500),
                5000 + rng.Next(-500, 500),
                clientId: $"player_{i}"));
        }

        // NPCs spread across the map
        for (int i = 0; i < NpcCount; i++)
        {
            objects.Add(new BenchObj(
                rng.Next(0, 10000),
                rng.Next(0, 10000)));
        }

        var lookup = objects.ToDictionary(o => o.InstanceId, o => o);
        _snapshots = [new WorldSnapshot(0, objects, lookup)];

        // Warm up — first tick populates visibility sets
        _tracker.Tick(_snapshots);
    }

    [Benchmark(Description = "Visibility Tick (steady state, no changes)")]
    public void TickSteadyState()
    {
        _tracker.Tick(_snapshots);
    }

    [Benchmark(Description = "GetVisibleEntities lookup")]
    public void GetVisible()
    {
        _tracker.GetVisibleEntities("player_0");
    }

    [Benchmark(Description = "GetObserversOf (reverse lookup)")]
    public void GetObservers()
    {
        // Pick an NPC instance ID — iterate observers
        foreach (var _ in _tracker.GetObserversOf("nonexistent")) { }
    }
}
