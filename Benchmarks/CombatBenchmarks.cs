using System.Numerics;
using Altruist.Gaming.Combat;
using Altruist.Gaming.ThreeD;
using Altruist.ThreeD.Numerics;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Altruist.Benchmarks;

/// <summary>
/// Benchmarks the combat system: damage calculation, sweep geometry (sphere/cone/line).
/// Sweep is the most expensive combat operation — spatial queries over all entities.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 5, iterationCount: 20)]
public class CombatBenchmarks
{
    private CombatService _combat = null!;
    private BenchCombatEntity _attacker = null!;
    private BenchCombatEntity _target = null!;
    private BenchCombatEntity[] _manyTargets = null!;

    public class BenchCombatEntity : WorldObject3D, ICombatEntity
    {
        public int Health { get; set; } = 100;
        public int MaxHealth { get; set; } = 100;
        public bool IsDead => Health <= 0;
        float ICombatEntity.X => Transform.Position.X;
        float ICombatEntity.Y => Transform.Position.Y;
        float ICombatEntity.Z => Transform.Position.Z;
        public int Atk { get; set; } = 50;
        public int Def { get; set; } = 10;
        public int GetAttackPower() => Atk;
        public int GetDefensePower() => Def;

        public BenchCombatEntity(float x, float y) : base(
            Transform3D.From(new Vector3(x, y, 0), Quaternion.Identity, Vector3.One))
        { }

        public override void Step(float dt, IGameWorldManager3D world) { }
    }

    [Params(100, 1000)]
    public int EntityCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        // Build world mock with N entities spread in a 10000x10000 area
        _manyTargets = new BenchCombatEntity[EntityCount];
        var rng = new Random(42);
        for (int i = 0; i < EntityCount; i++)
        {
            _manyTargets[i] = new BenchCombatEntity(rng.Next(0, 10000), rng.Next(0, 10000))
            {
                VirtualId = (uint)(i + 2),
            };
        }

        _attacker = new BenchCombatEntity(5000, 5000) { VirtualId = 1, Atk = 100 };
        _target = new BenchCombatEntity(5010, 5000) { VirtualId = 2, Health = 999999, Def = 10 };

        var world = new Mock<IGameWorldManager3D>();
        var allObjects = new List<IWorldObject3D> { _attacker };
        allObjects.AddRange(_manyTargets);
        world.Setup(w => w.FindAllObjects<IWorldObject3D>()).Returns(allObjects);

        var organizer = new Mock<IGameWorldOrganizer3D>();
        organizer.Setup(o => o.GetWorld(0)).Returns(world.Object);

        _combat = new CombatService(new DefaultDamageCalculator(), NullLoggerFactory.Instance, organizer.Object);
    }

    [Benchmark(Description = "Single Attack (calc + apply)")]
    public HitResult SingleAttack()
    {
        _target.Health = 999999; // reset
        return _combat.Attack(_attacker, _target);
    }

    [Benchmark(Description = "DefaultDamageCalculator.Calculate")]
    public int DamageCalc()
    {
        return new DefaultDamageCalculator().Calculate(_attacker, _target);
    }

    [Benchmark(Description = "Sweep Sphere r=500 (spatial query)")]
    public SweepResult SweepSphere()
    {
        foreach (var t in _manyTargets) t.Health = 100;
        return _combat.Sweep(_attacker, SweepQuery.Sphere(5000, 5000, 0, 500));
    }

    [Benchmark(Description = "Sweep Sphere r=2000 (large AoE)")]
    public SweepResult SweepSphereLarge()
    {
        foreach (var t in _manyTargets) t.Health = 100;
        return _combat.Sweep(_attacker, SweepQuery.Sphere(5000, 5000, 0, 2000));
    }

    [Benchmark(Description = "Sweep Cone 90° r=1000")]
    public SweepResult SweepCone()
    {
        foreach (var t in _manyTargets) t.Health = 100;
        return _combat.Sweep(_attacker, SweepQuery.Cone(5000, 5000, 0, 1000, 0f, 90f));
    }

    [Benchmark(Description = "Sweep Line r=2000")]
    public SweepResult SweepLine()
    {
        foreach (var t in _manyTargets) t.Health = 100;
        return _combat.Sweep(_attacker, SweepQuery.Line(5000, 5000, 0, 2000, 0f));
    }
}
