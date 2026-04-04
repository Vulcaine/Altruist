using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.ThreeD.Numerics;
using Moq;

namespace Tests.Gaming.World.ThreeD;

public class TestWorldObj : WorldObject3D
{
    public TestWorldObj(float x, float y, string clientId = "")
        : base(Transform3D.From(new Vector3(x, y, 0), Quaternion.Identity, Vector3.One))
    {
        ClientId = clientId;
    }

    public override void Step(float dt, IGameWorldManager3D world) { }
}

public class VisibilityTracker3DTests
{
    private VisibilityTracker3D CreateTracker(float viewRange = 5000f)
        => new(viewRange);

    private WorldSnapshot CreateSnapshot(Mock<IGameWorldManager3D> world, params IWorldObject3D[] objects)
    {
        var list = objects.Cast<ITypelessWorldObject>().ToList();
        var lookup = objects.ToDictionary(o => o.InstanceId, o => (ITypelessWorldObject)o);
        return new WorldSnapshot(world.Object.Index.Index, list, lookup);
    }

    private Mock<IGameWorldManager3D> CreateMockWorld()
    {
        var world = new Mock<IGameWorldManager3D>();
        var index = new Mock<IWorldIndex3D>();
        index.Setup(i => i.Index).Returns(0);
        index.Setup(i => i.Name).Returns("test");
        world.Setup(w => w.Index).Returns(index.Object);
        return world;
    }

    private (VisibilityTracker3D tracker, Mock<IGameWorldOrganizer3D> organizer) SetupTracker(
        Mock<IGameWorldManager3D> world, float viewRange = 5000f)
    {
        var tracker = CreateTracker(viewRange);
        var organizer = new Mock<IGameWorldOrganizer3D>();
        organizer.Setup(o => o.GetWorld(0)).Returns(world.Object);
        organizer.Setup(o => o.GetAllWorlds()).Returns([world.Object]);
        tracker.SetOrganizer(organizer.Object);
        return (tracker, organizer);
    }

    [Fact]
    public void Tick_ShouldFireOnEntityVisible_WhenEntityEntersRange()
    {
        var world = CreateMockWorld();
        var (tracker, _) = SetupTracker(world, 1000f);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc = new TestWorldObj(100, 0);

        VisibilityChange? visible = null;
        tracker.OnEntityVisible += v => visible = v;

        tracker.Tick([CreateSnapshot(world, player, npc)]);

        Assert.NotNull(visible);
        Assert.Equal("player1", visible.Value.ObserverClientId);
        Assert.Same(npc, visible.Value.Target);
    }

    [Fact]
    public void Tick_ShouldFireOnEntityInvisible_WhenEntityLeavesRange()
    {
        var world = CreateMockWorld();
        var (tracker, _) = SetupTracker(world, 1000f);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc = new TestWorldObj(100, 0);

        tracker.Tick([CreateSnapshot(world, player, npc)]);

        npc.Transform = Transform3D.From(new Vector3(5000, 5000, 0), Quaternion.Identity, Vector3.One);

        VisibilityChange? invisible = null;
        tracker.OnEntityInvisible += v => invisible = v;

        tracker.Tick([CreateSnapshot(world, player, npc)]);

        Assert.NotNull(invisible);
        Assert.Equal("player1", invisible.Value.ObserverClientId);
    }

    [Fact]
    public void Tick_ShouldNotFireVisible_ForSelf()
    {
        var world = CreateMockWorld();
        var (tracker, _) = SetupTracker(world);

        var player = new TestWorldObj(0, 0, clientId: "player1");

        VisibilityChange? visible = null;
        tracker.OnEntityVisible += v => visible = v;

        tracker.Tick([CreateSnapshot(world, player)]);

        Assert.Null(visible);
    }

    [Fact]
    public void Tick_ShouldNotFireDuplicate_WhenEntityStaysInRange()
    {
        var world = CreateMockWorld();
        var (tracker, _) = SetupTracker(world);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc = new TestWorldObj(100, 0);

        int visibleCount = 0;
        tracker.OnEntityVisible += _ => visibleCount++;

        tracker.Tick([CreateSnapshot(world, player, npc)]);
        tracker.Tick([CreateSnapshot(world, player, npc)]);

        Assert.Equal(1, visibleCount);
    }

    [Fact]
    public void GetVisibleEntities_ShouldReturnCurrentlyVisible()
    {
        var world = CreateMockWorld();
        var (tracker, _) = SetupTracker(world);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc1 = new TestWorldObj(100, 0);
        var npc2 = new TestWorldObj(200, 0);

        tracker.Tick([CreateSnapshot(world, player, npc1, npc2)]);

        var visible = tracker.GetVisibleEntities("player1");
        Assert.NotNull(visible);
        Assert.Contains(npc1.InstanceId, visible);
        Assert.Contains(npc2.InstanceId, visible);
    }

    [Fact]
    public void GetObserversOf_ShouldReturnAllObservers()
    {
        var world = CreateMockWorld();
        var (tracker, _) = SetupTracker(world);

        var p1 = new TestWorldObj(0, 0, clientId: "player1");
        var p2 = new TestWorldObj(10, 0, clientId: "player2");
        var npc = new TestWorldObj(50, 0);

        tracker.Tick([CreateSnapshot(world, p1, p2, npc)]);

        var observers = tracker.GetObserversOf(npc.InstanceId).ToList();
        Assert.Contains("player1", observers);
        Assert.Contains("player2", observers);
    }

    [Fact]
    public void ViewRange_ShouldRespectConfiguredDistance()
    {
        var world = CreateMockWorld();
        var (tracker, _) = SetupTracker(world, 100f);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var farNpc = new TestWorldObj(200, 0);

        VisibilityChange? visible = null;
        tracker.OnEntityVisible += v => visible = v;

        tracker.Tick([CreateSnapshot(world, player, farNpc)]);

        Assert.Null(visible);
    }

    [Fact]
    public void RefreshObserver_ShouldAllowRefire()
    {
        var world = CreateMockWorld();
        var (tracker, _) = SetupTracker(world);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc = new TestWorldObj(100, 0);

        int visibleCount = 0;
        tracker.OnEntityVisible += _ => visibleCount++;

        tracker.Tick([CreateSnapshot(world, player, npc)]);
        Assert.Equal(1, visibleCount);

        tracker.RefreshObserver("player1");
        tracker.Tick([CreateSnapshot(world, player, npc)]);
        Assert.Equal(2, visibleCount);
    }

    [Fact]
    public void RemoveObserver_ShouldFireInvisibleForAllVisible()
    {
        var world = CreateMockWorld();
        var (tracker, organizer) = SetupTracker(world);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc = new TestWorldObj(100, 0);

        tracker.Tick([CreateSnapshot(world, player, npc)]);

        var list = new List<IWorldObject3D> { player, npc };
        var lookup = list.ToDictionary(o => o.InstanceId, o => o);
        world.Setup(w => w.GetCachedSnapshot())
            .Returns((list as IReadOnlyList<IWorldObject3D>, lookup as IReadOnlyDictionary<string, IWorldObject3D>));

        int invisibleCount = 0;
        tracker.OnEntityInvisible += _ => invisibleCount++;

        tracker.RemoveObserver("player1");

        Assert.True(invisibleCount > 0);
    }

    [Fact]
    public void RemoveObserver_ShouldCleanupState()
    {
        var world = CreateMockWorld();
        var (tracker, organizer) = SetupTracker(world);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc = new TestWorldObj(100, 0);
        tracker.Tick([CreateSnapshot(world, player, npc)]);

        var list = new List<IWorldObject3D>();
        var lookup = new Dictionary<string, IWorldObject3D>();
        world.Setup(w => w.GetCachedSnapshot())
            .Returns((list as IReadOnlyList<IWorldObject3D>, lookup as IReadOnlyDictionary<string, IWorldObject3D>));

        tracker.RemoveObserver("player1");

        Assert.Null(tracker.GetVisibleEntities("player1"));
    }
}
