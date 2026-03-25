using System.Numerics;
using Altruist.Gaming.Combat;
using Altruist.Gaming.ThreeD;
using Altruist.ThreeD.Numerics;
using Microsoft.Extensions.Logging.Abstractions;

namespace Tests.Gaming.Combat;

/// <summary>
/// Implements both ICombatEntity and IWorldObject3D so it works with
/// CombatService.Sweep which iterates world.FindAllObjects&lt;IWorldObject3D&gt;()
/// and casts to ICombatEntity.
/// </summary>
public class TestCombatEntity : WorldObject3D, ICombatEntity
{
    public int Health { get; set; } = 100;
    public int MaxHealth { get; set; } = 100;
    public bool IsDead => Health <= 0;
    float ICombatEntity.X => Transform.Position.X;
    float ICombatEntity.Y => Transform.Position.Y;
    float ICombatEntity.Z => Transform.Position.Z;
    public int AttackPower { get; set; } = 50;
    public int DefensePower { get; set; } = 10;

    public int GetAttackPower() => AttackPower;
    public int GetDefensePower() => DefensePower;

    private TestCombatEntity(Transform3D transform) : base(transform) { }

    public override void Step(float dt, IGameWorldManager3D world) { }

    public static TestCombatEntity Create(uint vid, int hp = 100, float x = 0, float y = 0, float z = 0, int atk = 50, int def = 10)
    {
        var transform = Transform3D.From(new Vector3(x, y, z), Quaternion.Identity, Vector3.One);
        var e = new TestCombatEntity(transform)
        {
            VirtualId = vid,
            Health = hp,
            MaxHealth = hp,
            AttackPower = atk,
            DefensePower = def,
        };
        return e;
    }
}

#region DefaultDamageCalculator

public class DefaultDamageCalculatorTests
{
    private readonly DefaultDamageCalculator _calc = new();

    [Fact]
    public void Calculate_ShouldReturnAttackMinusDefense()
    {
        var attacker = TestCombatEntity.Create(1, atk: 100, def: 0);
        var target = TestCombatEntity.Create(2, atk: 0, def: 30);

        var damage = _calc.Calculate(attacker, target);

        Assert.Equal(70, damage);
    }

    [Fact]
    public void Calculate_ShouldReturnMinimum1_WhenDefenseExceedsAttack()
    {
        var attacker = TestCombatEntity.Create(1, atk: 10, def: 0);
        var target = TestCombatEntity.Create(2, atk: 0, def: 50);

        var damage = _calc.Calculate(attacker, target);

        Assert.Equal(1, damage);
    }

    [Fact]
    public void Calculate_ShouldReturnMinimum1_WhenEqual()
    {
        var attacker = TestCombatEntity.Create(1, atk: 50, def: 0);
        var target = TestCombatEntity.Create(2, atk: 0, def: 50);

        var damage = _calc.Calculate(attacker, target);

        Assert.Equal(1, damage);
    }
}

#endregion

#region CombatService

public class CombatServiceTests
{
    private CombatService CreateService(IDamageCalculator? calc = null)
    {
        calc ??= new DefaultDamageCalculator();
        return new CombatService(calc, NullLoggerFactory.Instance);
    }

    [Fact]
    public void Attack_ShouldReduceTargetHealth()
    {
        var service = CreateService();
        var attacker = TestCombatEntity.Create(1, atk: 60, def: 0);
        var target = TestCombatEntity.Create(2, hp: 100, atk: 0, def: 10);

        var result = service.Attack(attacker, target);

        Assert.True(result.Damage > 0);
        Assert.True(target.Health < 100);
    }

    [Fact]
    public void Attack_OnDeadTarget_ShouldReturnMiss()
    {
        var service = CreateService();
        var attacker = TestCombatEntity.Create(1, atk: 100);
        var target = TestCombatEntity.Create(2, hp: 0);

        var result = service.Attack(attacker, target);

        Assert.Equal(0, result.Damage);
        Assert.Equal(DamageFlags.Miss, result.Flags);
        Assert.False(result.Killed);
    }

    [Fact]
    public void Attack_ShouldKillTarget_WhenHealthReachesZero()
    {
        var service = CreateService();
        var attacker = TestCombatEntity.Create(1, atk: 200);
        var target = TestCombatEntity.Create(2, hp: 10, def: 0);

        var result = service.Attack(attacker, target);

        Assert.True(result.Killed);
        Assert.Equal(0, target.Health);
    }

    [Fact]
    public void ApplyDamage_ShouldReduceHealthByExactAmount()
    {
        var service = CreateService();
        var source = TestCombatEntity.Create(1);
        var target = TestCombatEntity.Create(2, hp: 100);

        service.ApplyDamage(source, target, 50);

        Assert.Equal(50, target.Health);
    }

    [Fact]
    public void ApplyDamage_ShouldClampHealthToZero()
    {
        var service = CreateService();
        var source = TestCombatEntity.Create(1);
        var target = TestCombatEntity.Create(2, hp: 100);

        service.ApplyDamage(source, target, 200);

        Assert.Equal(0, target.Health);
    }

    [Fact]
    public void ApplyDamage_OnDeadTarget_ShouldReturnMiss()
    {
        var service = CreateService();
        var source = TestCombatEntity.Create(1);
        var target = TestCombatEntity.Create(2, hp: 0);

        var result = service.ApplyDamage(source, target, 50);

        Assert.Equal(0, result.Damage);
        Assert.Equal(DamageFlags.Miss, result.Flags);
    }

    [Fact]
    public void OnHit_ShouldFireOnSuccessfulAttack()
    {
        var service = CreateService();
        HitEvent? firedEvent = null;
        service.OnHit += e => firedEvent = e;

        var attacker = TestCombatEntity.Create(1, atk: 60);
        var target = TestCombatEntity.Create(2, hp: 100, def: 10);

        service.Attack(attacker, target);

        Assert.NotNull(firedEvent);
        Assert.Same(attacker, firedEvent.Attacker);
        Assert.Same(target, firedEvent.Target);
        Assert.True(firedEvent.Damage > 0);
    }

    [Fact]
    public void OnDeath_ShouldFireWhenTargetKilled()
    {
        var service = CreateService();
        DeathEvent? firedEvent = null;
        service.OnDeath += e => firedEvent = e;

        var attacker = TestCombatEntity.Create(1, atk: 200);
        var target = TestCombatEntity.Create(2, hp: 10, def: 0);

        service.Attack(attacker, target);

        Assert.NotNull(firedEvent);
        Assert.Same(target, firedEvent.Entity);
        Assert.Same(attacker, firedEvent.Killer);
    }

    [Fact]
    public void OnDeath_ShouldNotFire_WhenTargetSurvives()
    {
        var service = CreateService();
        DeathEvent? firedEvent = null;
        service.OnDeath += e => firedEvent = e;

        var attacker = TestCombatEntity.Create(1, atk: 20);
        var target = TestCombatEntity.Create(2, hp: 100, def: 10);

        service.Attack(attacker, target);

        Assert.Null(firedEvent);
    }

    [Fact]
    public void Kill_ShouldSetHealthToZero_AndFireDeath()
    {
        var service = CreateService();
        DeathEvent? firedEvent = null;
        service.OnDeath += e => firedEvent = e;

        var entity = TestCombatEntity.Create(1, hp: 100);

        service.Kill(entity);

        Assert.Equal(0, entity.Health);
        Assert.NotNull(firedEvent);
        Assert.Same(entity, firedEvent.Entity);
        Assert.Null(firedEvent.Killer);
    }

    [Fact]
    public void Kill_WithKiller_ShouldIncludeKillerInEvent()
    {
        var service = CreateService();
        DeathEvent? firedEvent = null;
        service.OnDeath += e => firedEvent = e;

        var killer = TestCombatEntity.Create(1);
        var victim = TestCombatEntity.Create(2, hp: 100);

        service.Kill(victim, killer);

        Assert.NotNull(firedEvent);
        Assert.Same(killer, firedEvent.Killer);
    }
}

#endregion

#region SweepGeometry

public class SweepGeometryTests
{
    private CombatService CreateServiceWithWorld(params TestCombatEntity[] entities)
    {
        // For sweep tests without a world organizer, the service finds 0 entities.
        // We test the geometry math indirectly via a world mock.
        // Since CombatService.FindEntitiesInSweep uses _worldOrganizer?.GetWorld(0),
        // and the world organizer is optional, we need to use Moq.
        var world = new Moq.Mock<Altruist.Gaming.ThreeD.IGameWorldManager3D>();
        var objects = entities.Cast<Altruist.Gaming.ThreeD.IWorldObject3D>().ToList();
        world.Setup(w => w.FindAllObjects<Altruist.Gaming.ThreeD.IWorldObject3D>()).Returns(objects);

        var organizer = new Moq.Mock<Altruist.Gaming.ThreeD.IGameWorldOrganizer3D>();
        organizer.Setup(o => o.GetWorld(0)).Returns(world.Object);

        return new CombatService(
            new DefaultDamageCalculator(),
            NullLoggerFactory.Instance,
            organizer.Object);
    }

    [Fact]
    public void Sweep_Sphere_ShouldHitEntitiesInRadius()
    {
        var attacker = TestCombatEntity.Create(1, x: 0, y: 0);
        var target = TestCombatEntity.Create(2, hp: 100, x: 50, y: 0, def: 0);
        var service = CreateServiceWithWorld(attacker, target);

        var query = SweepQuery.Sphere(0, 0, 0, 100);
        var result = service.Sweep(attacker, query, 20);

        Assert.Single(result.Hits);
        Assert.Equal(80, target.Health);
    }

    [Fact]
    public void Sweep_Sphere_ShouldMissEntitiesOutsideRadius()
    {
        var attacker = TestCombatEntity.Create(1, x: 0, y: 0);
        var far = TestCombatEntity.Create(2, hp: 100, x: 500, y: 500);
        var service = CreateServiceWithWorld(attacker, far);

        var query = SweepQuery.Sphere(0, 0, 0, 100);
        var result = service.Sweep(attacker, query, 20);

        Assert.Empty(result.Hits);
        Assert.Equal(100, far.Health);
    }

    [Fact]
    public void Sweep_ShouldSkipSelf()
    {
        var attacker = TestCombatEntity.Create(1, hp: 100, x: 0, y: 0);
        var service = CreateServiceWithWorld(attacker);

        var query = SweepQuery.Sphere(0, 0, 0, 100);
        var result = service.Sweep(attacker, query, 50);

        Assert.Empty(result.Hits);
        Assert.Equal(100, attacker.Health);
    }

    [Fact]
    public void Sweep_ShouldSkipDeadTargets()
    {
        var attacker = TestCombatEntity.Create(1, x: 0, y: 0);
        var dead = TestCombatEntity.Create(2, hp: 0, x: 10, y: 0);
        var service = CreateServiceWithWorld(attacker, dead);

        var query = SweepQuery.Sphere(0, 0, 0, 100);
        var result = service.Sweep(attacker, query, 20);

        Assert.Empty(result.Hits);
    }

    [Fact]
    public void Sweep_MaxTargets_ShouldLimitHits()
    {
        var attacker = TestCombatEntity.Create(1, x: 0, y: 0);
        var t1 = TestCombatEntity.Create(2, hp: 100, x: 10, y: 0, def: 0);
        var t2 = TestCombatEntity.Create(3, hp: 100, x: 20, y: 0, def: 0);
        var t3 = TestCombatEntity.Create(4, hp: 100, x: 30, y: 0, def: 0);
        var service = CreateServiceWithWorld(attacker, t1, t2, t3);

        var query = SweepQuery.Sphere(0, 0, 0, 100) with { MaxTargets = 2 };
        var result = service.Sweep(attacker, query, 10);

        Assert.Equal(2, result.Hits.Count);
    }

    [Fact]
    public void Sweep_Line_ShouldHitEntitiesAlongLine()
    {
        var attacker = TestCombatEntity.Create(1, x: 0, y: 0);
        // Target directly ahead (direction = 0 = east)
        var ahead = TestCombatEntity.Create(2, hp: 100, x: 50, y: 0, def: 0);
        // Target perpendicular and far from line
        var side = TestCombatEntity.Create(3, hp: 100, x: 0, y: 500);
        var service = CreateServiceWithWorld(attacker, ahead, side);

        var query = SweepQuery.Line(0, 0, 0, 200, 0f); // direction = 0 = east
        var result = service.Sweep(attacker, query, 10);

        Assert.Single(result.Hits);
        Assert.Equal(90, ahead.Health);
    }
}

#endregion
