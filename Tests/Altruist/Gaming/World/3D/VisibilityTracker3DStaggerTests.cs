using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Moq;

namespace Tests.Gaming.World.ThreeD;

public class VisibilityTracker3DStaggerTests
{
    private sealed class Mob : TestWorldObj, IHibernatable
    {
        public Mob(float x) : base(x, 0) { }
        public bool IsHibernated { get; set; }
        public bool CanHibernate => true;
        public void OnHibernate() { }
        public void OnWake() { }
    }

    private static WorldSnapshot Snapshot(params IWorldObject3D[] objects)
        => new(0, objects.Cast<ITypelessWorldObject>().ToList(), objects.ToDictionary(o => o.InstanceId, o => (ITypelessWorldObject)o));

    [Fact]
    public void Entity_seen_by_an_observer_skipped_this_tick_is_not_hibernated()
    {
        var world = new Mock<IGameWorldManager3D>();
        var index = new Mock<IWorldIndex3D>();
        index.Setup(i => i.Index).Returns(0);
        world.Setup(w => w.Index).Returns(index.Object);
        var hibernation = new Mock<IEntityHibernationService>();
        var tracker = new VisibilityTracker3D(1000f, hibernation.Object);
        var organizer = new Mock<IGameWorldOrganizer3D>();
        organizer.Setup(o => o.GetWorld(0)).Returns(world.Object);
        organizer.Setup(o => o.GetAllWorlds()).Returns([world.Object]);
        tracker.SetOrganizer(organizer.Object);

        var observers = Enumerable.Range(0, 8).Select(i => new TestWorldObj(i * 100_000f, 0, clientId: $"c{i}")).ToArray();
        foreach (var o in observers) Assert.True(tracker.Observe(o));
        var mob = new Mob(100f);

        tracker.Tick([Snapshot(observers[0], mob)]);
        var all = observers.Cast<IWorldObject3D>().Append(mob).ToArray();
        tracker.Tick([Snapshot(all)]);
        tracker.Tick([Snapshot(all)]);

        hibernation.Verify(h => h.Hibernate(mob.InstanceId, It.IsAny<string>(), It.IsAny<float>(), It.IsAny<float>(),
            It.IsAny<float>(), It.IsAny<int>(), It.IsAny<IHibernatable>()), Times.Never);
    }
}
