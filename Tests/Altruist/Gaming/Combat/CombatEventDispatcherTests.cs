using System.Numerics;
using Altruist;
using Altruist.Gaming;
using Altruist.Gaming.Combat;
using Altruist.Gaming.ThreeD;
using Altruist.ThreeD.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Gaming.CombatEvents;

public class CombatEventTestEntity : WorldObject3D, ICombatEntity
{
    public int Health { get; set; } = 100;
    public int MaxHealth { get; set; } = 100;
    public bool IsDead => Health <= 0;
    public int AttackPower { get; set; } = 50;
    public int DefensePower { get; set; } = 10;
    float ICombatEntity.X => Transform.Position.X;
    float ICombatEntity.Y => Transform.Position.Y;
    float ICombatEntity.Z => Transform.Position.Z;

    public CombatEventTestEntity(uint vid, int hp = 100, float x = 0, float y = 0, float z = 0)
        : base(Transform3D.From(new Vector3(x, y, z), Quaternion.Identity, Vector3.One))
    {
        VirtualId = vid;
        Health = hp;
        MaxHealth = hp;
    }

    public int GetAttackPower() => AttackPower;
    public int GetDefensePower() => DefensePower;
    public override void Step(float dt, IGameWorldManager3D world) { }
}

public sealed class CombatEventActorA : CombatEventTestEntity
{
    public CombatEventActorA(uint vid) : base(vid) { }
}

public sealed class CombatEventActorB : CombatEventTestEntity
{
    public CombatEventActorB(uint vid) : base(vid) { }
}

public sealed class CustomCombatEvent : ICombatEventPayload
{
    public int Value { get; init; }
}

[CombatHandler]
public sealed class CustomCombatEventHandler
{
    public CustomCombatEvent? Custom { get; private set; }
    public CombatEventTestEntity? Primary { get; private set; }
    public CombatEventTestEntity? Secondary { get; private set; }
    public HitEvent? Hit { get; private set; }
    public DeathEvent? Death { get; private set; }
    public SweepEvent? Sweep { get; private set; }

    [CombatEvent(typeof(CustomCombatEvent))]
    public void OnCustom(CustomCombatEvent evt, CombatEventActorA primary, CombatEventActorB secondary)
    {
        Custom = evt;
        Primary = primary;
        Secondary = secondary;
    }

    [CombatEvent(typeof(HitEvent))]
    public void OnHit(HitEvent evt, CombatEventTestEntity primary, CombatEventTestEntity secondary)
    {
        Hit = evt;
        Primary = primary;
        Secondary = secondary;
    }

    [CombatEvent(typeof(DeathEvent))]
    public void OnDeath(DeathEvent evt, CombatEventTestEntity dead)
    {
        Death = evt;
        Primary = dead;
    }

    [CombatEvent(typeof(SweepEvent))]
    public void OnSweep(SweepEvent evt, CombatEventTestEntity attacker)
    {
        Sweep = evt;
        Primary = attacker;
    }
}

public sealed class RecordingCombatEventDispatcher : ICombatEventDispatcher
{
    public readonly List<ICombatEventPayload> Payloads = [];
    public readonly List<(object Primary, object? Secondary)> Actors = [];

    public void Dispatch<TEvent>(TEvent payload, object primary) where TEvent : ICombatEventPayload
    {
        Payloads.Add(payload);
        Actors.Add((primary, null));
    }

    public void Dispatch<TEvent>(TEvent payload, object primary, object secondary) where TEvent : ICombatEventPayload
    {
        Payloads.Add(payload);
        Actors.Add((primary, secondary));
    }
}

public class CombatEventDispatcherTests
{
    [Fact]
    public void Dispatch_ShouldInvokePayloadFirstCustomHandler_AndSupportSymmetricActorOrder()
    {
        CombatEventHandlerRegistry.ClearForTests();
        var handler = new CustomCombatEventHandler();
        CombatEventHandlerDiscovery.RegisterCombatHandlers(
            [typeof(CustomCombatEventHandler).Assembly],
            type => type == typeof(CustomCombatEventHandler) ? handler : null,
            NullLogger.Instance);

        var dispatcher = new CombatEventDispatcher(NullLoggerFactory.Instance);
        var primary = new CombatEventActorA(1);
        var secondary = new CombatEventActorB(2);
        var evt = new CustomCombatEvent { Value = 7 };

        dispatcher.Dispatch(evt, secondary, primary);

        Assert.Same(evt, handler.Custom);
        Assert.Same(primary, handler.Primary);
        Assert.Same(secondary, handler.Secondary);
    }

    [Fact]
    public void Dispatch_ShouldSupportOneActorEvents()
    {
        CombatEventHandlerRegistry.ClearForTests();
        var handler = new CustomCombatEventHandler();
        CombatEventHandlerDiscovery.RegisterCombatHandlers(
            [typeof(CustomCombatEventHandler).Assembly],
            type => type == typeof(CustomCombatEventHandler) ? handler : null,
            NullLogger.Instance);

        var dispatcher = new CombatEventDispatcher(NullLoggerFactory.Instance);
        var dead = new CombatEventTestEntity(1, hp: 0);
        var evt = new DeathEvent(dead, null, 1, 2, 3);

        dispatcher.Dispatch(evt, dead);

        Assert.Same(evt, handler.Death);
        Assert.Same(dead, handler.Primary);
    }

    [Fact]
    public void CombatService_ShouldDispatchHitAndDeathEvents()
    {
        var recorder = new RecordingCombatEventDispatcher();
        var service = new CombatService(NullLoggerFactory.Instance, combatEvents: recorder);
        var attacker = new CombatEventTestEntity(1) { AttackPower = 100 };
        var target = new CombatEventTestEntity(2, hp: 10) { DefensePower = 0 };

        service.Attack(attacker, target);

        var hit = Assert.IsType<HitEvent>(recorder.Payloads[0]);
        Assert.True(hit.Flags.IsSet(DamageFlags.Killed));
        Assert.Same(attacker, recorder.Actors[0].Primary);
        Assert.Same(target, recorder.Actors[0].Secondary);

        var death = Assert.IsType<DeathEvent>(recorder.Payloads[1]);
        Assert.Same(target, death.Entity);
        Assert.Same(attacker, death.Killer);
    }

    [Fact]
    public void CombatService_ShouldDispatchSweepEvent()
    {
        var recorder = new RecordingCombatEventDispatcher();
        var attacker = new CombatEventTestEntity(1, x: 0, y: 0);
        var target = new CombatEventTestEntity(2, hp: 100, x: 10, y: 0);
        var service = CreateServiceWithWorld(recorder, attacker, target);

        var query = SweepQuery3D.Sphere(0, 0, 0, 100);
        service.Sweep(attacker, query, 20);

        Assert.Contains(recorder.Payloads, static p => p is SweepEvent);
    }

    private static CombatService CreateServiceWithWorld(
        ICombatEventDispatcher dispatcher,
        params CombatEventTestEntity[] entities)
    {
        var world = new Mock<IGameWorldManager3D>();
        var objects = entities.Cast<IWorldObject3D>().ToList();
        var lookup = objects.ToDictionary(o => o.InstanceId, o => o);
        world.Setup(w => w.GetCachedSnapshot())
            .Returns((objects as IReadOnlyList<IWorldObject3D>,
                      lookup as IReadOnlyDictionary<string, IWorldObject3D>));

        var organizer = new Mock<IGameWorldOrganizer3D>();
        organizer.Setup(o => o.GetWorld(0)).Returns(world.Object);

        return new CombatService(
            NullLoggerFactory.Instance,
            worldOrganizer: organizer.Object,
            combatEvents: dispatcher);
    }
}
