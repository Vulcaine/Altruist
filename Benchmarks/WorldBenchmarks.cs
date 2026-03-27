using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Networking;
using Altruist.ThreeD.Numerics;
using BenchmarkDotNet.Attributes;

namespace Altruist.Benchmarks;

/// <summary>
/// Benchmarks world object iteration and snapshot creation.
/// These are the foundation operations — every subsystem (AI, sync, visibility, collision)
/// iterates all objects at least once per tick.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class WorldBenchmarks
{
    private List<ITypelessWorldObject> _objects = null!;
    private Dictionary<string, ITypelessWorldObject> _lookup = null!;

    public class BenchWorldObj : WorldObject3D
    {
        public BenchWorldObj(float x, float y)
            : base(Transform3D.From(new Vector3(x, y, 0), Quaternion.Identity, Vector3.One))
        { }

        public override void Step(float dt, IGameWorldManager3D world) { }
    }

    [Synchronized]
    public class BenchSyncObj : WorldObject3D, Altruist.Networking.ISynchronizedEntity
    {
        public string ClientId { get; set; } = "";
        [Altruist.Networking.Synced(0)] public int Hp { get; set; } = 100;

        public BenchSyncObj(float x, float y, string clientId)
            : base(Transform3D.From(new Vector3(x, y, 0), Quaternion.Identity, Vector3.One))
        { ClientId = clientId; }

        public override void Step(float dt, IGameWorldManager3D world) { }
    }

    [Params(1000, 5000)]
    public int ObjectCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(42);
        _objects = new List<ITypelessWorldObject>(ObjectCount);

        for (int i = 0; i < ObjectCount; i++)
        {
            if (i % 10 == 0) // 10% are sync entities
                _objects.Add(new BenchSyncObj(rng.Next(0, 10000), rng.Next(0, 10000), $"p_{i}"));
            else
                _objects.Add(new BenchWorldObj(rng.Next(0, 10000), rng.Next(0, 10000)));
        }

        _lookup = _objects.ToDictionary(o => o.InstanceId, o => o);
    }

    [Benchmark(Description = "WorldSnapshot creation")]
    public WorldSnapshot CreateSnapshot()
    {
        return new WorldSnapshot(0, _objects, _lookup);
    }

    [Benchmark(Description = "Iterate all + filter ISynchronizedEntity")]
    public int FilterSyncEntities()
    {
        int count = 0;
        for (int i = 0; i < _objects.Count; i++)
        {
            if (_objects[i] is Altruist.Networking.ISynchronizedEntity)
                count++;
        }
        return count;
    }

    [Benchmark(Description = "Iterate all + filter IAIBehaviorEntity")]
    public int FilterAIEntities()
    {
        int count = 0;
        for (int i = 0; i < _objects.Count; i++)
        {
            if (_objects[i] is IAIBehaviorEntity)
                count++;
        }
        return count;
    }

    [Benchmark(Description = "Dictionary lookup by InstanceId")]
    public ITypelessWorldObject? LookupById()
    {
        return _lookup.TryGetValue(_objects[ObjectCount / 2].InstanceId, out var obj) ? obj : null;
    }

    [Benchmark(Description = "Iterate all + distance check (r=2000)")]
    public int DistanceCheck()
    {
        int count = 0;
        float cx = 5000, cy = 5000;
        float rangeSq = 2000 * 2000;

        for (int i = 0; i < _objects.Count; i++)
        {
            if (_objects[i] is IWorldObject3D obj3d)
            {
                var p = obj3d.Transform.Position;
                float dx = p.X - cx;
                float dy = p.Y - cy;
                if (dx * dx + dy * dy <= rangeSq)
                    count++;
            }
        }
        return count;
    }
}
