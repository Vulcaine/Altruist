using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.ThreeD.Numerics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Altruist.Benchmarks;

/// <summary>
/// Benchmarks the spatial collision dispatcher.
/// Measures: O(n²) overlap detection, enter/stay/exit lifecycle, dispatch overhead.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 20)]
public class CollisionBenchmarks
{
    private SpatialCollisionDispatcher _dispatcher = null!;
    private Mock<IGameWorldManager3D> _world = null!;

    public class CollObj : WorldObject3D
    {
        public CollObj(float x, float y, float colliderRadius = 100)
            : base(Transform3D.From(new Vector3(x, y, 0), Quaternion.Identity, Vector3.One))
        {
            if (colliderRadius > 0)
                ColliderDescriptors = [Altruist.Physx.ThreeD.PhysxCollider3D.CreateSphere(colliderRadius, isTrigger: true)];
        }

        public override void Step(float dt, IGameWorldManager3D world) { }
    }

    [Params(100, 500)]
    public int EntityCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _dispatcher = new SpatialCollisionDispatcher(NullLoggerFactory.Instance);

        var rng = new Random(42);
        var objects = new List<IWorldObject3D>();

        for (int i = 0; i < EntityCount; i++)
        {
            objects.Add(new CollObj(
                rng.Next(0, 5000),
                rng.Next(0, 5000),
                colliderRadius: 200));
        }

        _world = new Mock<IGameWorldManager3D>();
        var lookup = objects.ToDictionary(o => o.InstanceId, o => o);
        _world.Setup(w => w.GetCachedSnapshot())
            .Returns((objects as IReadOnlyList<IWorldObject3D>,
                      lookup as IReadOnlyDictionary<string, IWorldObject3D>));

        // Warm up — first tick establishes overlaps
        _dispatcher.Tick(_world.Object);
    }

    [Benchmark(Description = "Collision Tick (full O(n²) overlap detection)")]
    public void TickFull()
    {
        _dispatcher.Tick(_world.Object);
    }

    [Benchmark(Description = "DispatchHit (single pair, no handlers)")]
    public void DispatchHitNoHandlers()
    {
        var a = new CollObj(0, 0);
        var b = new CollObj(10, 0);
        _dispatcher.DispatchHit(a, b);
    }

    [Benchmark(Description = "RemoveEntity")]
    public void RemoveEntity()
    {
        _dispatcher.RemoveEntity("nonexistent_id");
    }
}
