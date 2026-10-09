using Altruist.Gaming.Combat;
using Altruist.Gaming.ThreeD;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Tests.Gaming.CombatEvents;

namespace Tests.Gaming.Combat;

public sealed class CombatSubclassActor : CombatEventTestEntity
{
    public CombatSubclassActor(uint vid) : base(vid) { }
}

public sealed class SubclassProbeEvent : ICombatEventPayload { }

[Collection("CombatRegistry")]
public class CombatRegressionTests
{
    private sealed class Probe
    {
        public int Calls;
        public void On(SubclassProbeEvent e, CombatEventTestEntity a) => Calls++;
    }

    private static CombatEventHandlerRegistry.HandlerDescriptor Descriptor(Probe probe) => new(
        typeof(Probe), typeof(SubclassProbeEvent), typeof(CombatEventTestEntity), null,
        (Action<object, object, object?>)((p, a, _) => probe.On((SubclassProbeEvent)p, (CombatEventTestEntity)a)),
        typeof(Probe).GetMethod(nameof(Probe.On)));

    [Fact]
    public void Lethal_hit_packet_carries_the_killed_flag()
    {
        var target = new CombatEventTestEntity(7, hp: 0);
        var packet = DamagePacket.From(new HitResult(target, 30, DamageFlags.Critical, killed: true));

        Assert.Equal((uint)(DamageFlags.Critical | DamageFlags.Killed), packet.Flags);
        Assert.Equal(7u, packet.VID);
    }

    [Fact]
    public void Killing_a_dead_entity_raises_no_second_death_event()
    {
        var service = new CombatService(NullLoggerFactory.Instance);
        var deaths = 0;
        service.OnDeath += _ => deaths++;
        var entity = new CombatEventTestEntity(1);

        service.Kill(entity);
        service.Kill(entity);

        Assert.Equal(1, deaths);
    }

    [Fact]
    public void Lethal_damage_still_raises_one_death_event()
    {
        var service = new CombatService(NullLoggerFactory.Instance);
        var deaths = 0;
        service.OnDeath += _ => deaths++;

        service.ApplyDamage(new CombatEventTestEntity(1), new CombatEventTestEntity(2, hp: 10), 50);

        Assert.Equal(1, deaths);
    }

    [Fact]
    public void Handler_for_a_base_actor_type_fires_for_a_subclass()
    {
        CombatEventHandlerRegistry.ClearForTests();
        var probe = new Probe();
        CombatEventHandlerRegistry.Register(Descriptor(probe));

        new CombatEventDispatcher(NullLoggerFactory.Instance).Dispatch(new SubclassProbeEvent(), new CombatSubclassActor(1));

        Assert.Equal(1, probe.Calls);
    }

    [Fact]
    public void Registering_the_same_handler_method_again_replaces_it()
    {
        CombatEventHandlerRegistry.ClearForTests();
        var first = new Probe();
        var second = new Probe();
        CombatEventHandlerRegistry.Register(Descriptor(first));
        CombatEventHandlerRegistry.Register(Descriptor(second));

        new CombatEventDispatcher(NullLoggerFactory.Instance).Dispatch(new SubclassProbeEvent(), new CombatEventTestEntity(1));

        Assert.Equal(0, first.Calls);
        Assert.Equal(1, second.Calls);
        Assert.Equal(1, CombatEventHandlerRegistry.TotalHandlerCount);
    }

    [Fact]
    public void Planar_sphere_sweep_ignores_height_in_a_crowded_world()
    {
        var attacker = new CombatEventTestEntity(1, x: 0, y: 0, z: 0);
        var high = new CombatEventTestEntity(2, x: 1, y: 900, z: 1);
        var crowd = Enumerable.Range(0, 60).Select(i => new CombatEventTestEntity((uint)(100 + i), x: 5000 + i, y: 0, z: 5000)).ToList();
        var all = new List<IWorldObject3D> { attacker, high };
        all.AddRange(crowd);
        var world = new Mock<IGameWorldManager3D>();
        world.Setup(w => w.GetCachedSnapshot()).Returns((all as IReadOnlyList<IWorldObject3D>,
            all.ToDictionary(o => o.InstanceId, o => o) as IReadOnlyDictionary<string, IWorldObject3D>));
        world.SetupGet(w => w.SnapshotVersion).Returns(1);
        var organizer = new Mock<IGameWorldOrganizer3D>();
        organizer.Setup(o => o.GetWorld(0)).Returns(world.Object);
        var service = new CombatService(NullLoggerFactory.Instance, worldOrganizer: organizer.Object);

        var result = service.Sweep(attacker, SweepQuery3D.Sphere(0, 0, 0, 10) with { Space = SweepSpace.PlanarXZ }, 5);

        Assert.Contains(result.Hits, h => h.Target == high);
    }
}
