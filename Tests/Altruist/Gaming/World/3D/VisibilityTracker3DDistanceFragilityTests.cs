using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.ThreeD.Numerics;
using Moq;

namespace Tests.Gaming.World.ThreeD;

/// <summary>
/// Verifies the 3D-sphere visibility semantics. With a tight ViewRange a Y
/// delta between observer and target eats into the radius budget — that is
/// the intended behavior of a sphere check, not a bug. These tests pin
/// down the math so future refactors don't accidentally change the metric.
/// </summary>
public class VisibilityTracker3DDistanceFragilityTests
{
    private const float TightViewRange = 30f;

    private static Mock<IGameWorldManager3D> CreateMockWorld()
    {
        var world = new Mock<IGameWorldManager3D>();
        var index = new Mock<IWorldIndex3D>();
        index.Setup(i => i.Index).Returns(0);
        index.Setup(i => i.Name).Returns("test");
        world.Setup(w => w.Index).Returns(index.Object);
        return world;
    }

    private static (VisibilityTracker3D tracker, Mock<IGameWorldManager3D> world) SetupTracker(float viewRange)
    {
        var tracker = new VisibilityTracker3D(viewRange);
        var organizer = new Mock<IGameWorldOrganizer3D>();
        var world = CreateMockWorld();
        organizer.Setup(o => o.GetWorld(0)).Returns(world.Object);
        organizer.Setup(o => o.GetAllWorlds()).Returns([world.Object]);
        tracker.SetOrganizer(organizer.Object);
        return (tracker, world);
    }

    private static WorldSnapshot Snapshot(Mock<IGameWorldManager3D> world, params IWorldObject3D[] objects)
    {
        var list = objects.Cast<ITypelessWorldObject>().ToList();
        var lookup = objects.ToDictionary(o => o.InstanceId, o => (ITypelessWorldObject)o);
        return new WorldSnapshot(world.Object.Index.Index, list, lookup);
    }

    [Fact]
    public void MobYDropWithin3DRange_StaysVisible()
    {
        // Mob 5m horizontally from player. Player Y drops 25m. 3D distance:
        // sqrt(5² + 25²) = 25.5 — still inside the 30m sphere → visible.
        var (tracker, world) = SetupTracker(TightViewRange);
        var player = new TestWorldObj(0, 180, clientId: "p1");
        var mob = new TestWorldObj(5, 180);
        tracker.Observe(player);

        tracker.Tick([Snapshot(world, player, mob)]);
        Assert.Contains(mob.InstanceId, tracker.GetVisibleEntities("p1") ?? new HashSet<string>());

        player.Transform = player.Transform.WithPosition(new Position3D(0, 155, 0));

        VisibilityChange? invisibleEvent = null;
        tracker.OnEntityInvisible += e => invisibleEvent = e;
        tracker.Tick([Snapshot(world, player, mob)]);

        Assert.Null(invisibleEvent);
    }

    [Fact]
    public void MobYDropOutside3DRange_GoesInvisible()
    {
        // Mob 5m horizontally. Player Y drops 30m. 3D distance: sqrt(5² + 30²)
        // = 30.4 — outside the sphere → invisible. This is the sphere check
        // working as designed; the responsibility for "keep player and mobs
        // both close to terrain Y" lives upstream (KCC ground-snap, mob
        // terrain clamp). When that fails, this test fires.
        var (tracker, world) = SetupTracker(TightViewRange);
        var player = new TestWorldObj(0, 180, clientId: "p1");
        var mob = new TestWorldObj(5, 180);
        tracker.Observe(player);

        tracker.Tick([Snapshot(world, player, mob)]);
        Assert.Contains(mob.InstanceId, tracker.GetVisibleEntities("p1") ?? new HashSet<string>());

        player.Transform = player.Transform.WithPosition(new Position3D(0, 150, 0));

        VisibilityChange? invisibleEvent = null;
        tracker.OnEntityInvisible += e => invisibleEvent = e;
        tracker.Tick([Snapshot(world, player, mob)]);

        Assert.NotNull(invisibleEvent);
        Assert.Equal(mob.InstanceId, ((IWorldObject3D)invisibleEvent.Value.Target).InstanceId);
    }

    [Fact]
    public void MassDisappearance_FromYDrop_QuantifiedHere()
    {
        // Documents the production scenario: 30 mobs in a 10m XZ ring around
        // the player, all at Y=180. Player Y drops to 149 (the exact value
        // captured in production logs: obs=(352,149,791)). 3D distance to
        // every mob is sqrt(10² + 31²) ≈ 32.6, exceeding 30m → all 30 fire
        // Invisible in one tick. The fix is upstream: prevent the player
        // from ending up 31m below the surrounding mobs in the first place.
        var (tracker, world) = SetupTracker(TightViewRange);
        var player = new TestWorldObj(0, 180, clientId: "p1");
        var mobs = new List<TestWorldObj>();
        for (int i = 0; i < 30; i++)
        {
            float angle = (i / 30f) * MathF.Tau;
            mobs.Add(new TestWorldObj(MathF.Cos(angle) * 10f, 180f));
        }
        tracker.Observe(player);

        var allObjs = new[] { (IWorldObject3D)player }.Concat(mobs.Cast<IWorldObject3D>()).ToArray();
        tracker.Tick([Snapshot(world, allObjs)]);
        Assert.Equal(30, (tracker.GetVisibleEntities("p1") ?? new HashSet<string>()).Count);

        player.Transform = player.Transform.WithPosition(new Position3D(0, 149, 0));

        int invisibleCount = 0;
        tracker.OnEntityInvisible += _ => invisibleCount++;
        tracker.Tick([Snapshot(world, allObjs)]);

        Assert.Equal(30, invisibleCount);
        Assert.Equal(0, (tracker.GetVisibleEntities("p1") ?? new HashSet<string>()).Count);
    }
}
