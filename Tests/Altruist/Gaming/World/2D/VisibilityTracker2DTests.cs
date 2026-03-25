using Altruist.Gaming;
using Altruist.Gaming.TwoD;
using Altruist.TwoD.Numerics;
using Moq;

namespace Tests.Gaming.World.TwoD;

public class TestWorldObj2D : WorldObject2D
{
    public TestWorldObj2D(int x, int y, string clientId = "")
        : base(new Transform2D(Position2D.Of(x, y), Size2D.Of(1, 1), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)))
    {
        ClientId = clientId;
    }

    public override void Step(float dt, IGameWorldManager2D world) { }
}

public class VisibilityTracker2DTests
{
    private (VisibilityTracker2D tracker, Mock<IGameWorldOrganizer2D> organizer) SetupTracker(
        Mock<IGameWorldManager2D> world, float viewRange = 5000f)
    {
        var organizer = new Mock<IGameWorldOrganizer2D>();
        organizer.Setup(o => o.GetAllWorlds()).Returns([world.Object]);
        var tracker = new VisibilityTracker2D(organizer.Object, viewRange);
        return (tracker, organizer);
    }

    private Mock<IGameWorldManager2D> CreateMockWorld(params IWorldObject2D[] objects)
    {
        var world = new Mock<IGameWorldManager2D>();
        var index = new Mock<IWorldIndex2D>();
        index.Setup(i => i.Index).Returns(0);
        world.Setup(w => w.Index).Returns(index.Object);
        world.Setup(w => w.FindAllObjects<IWorldObject2D>()).Returns(objects.AsEnumerable());
        return world;
    }

    [Fact]
    public void Tick_ShouldFireOnEntityVisible_WhenEntityEntersRange()
    {
        var player = new TestWorldObj2D(0, 0, clientId: "player1");
        var npc = new TestWorldObj2D(100, 0);
        var world = CreateMockWorld(player, npc);
        var (tracker, _) = SetupTracker(world, 1000f);

        VisibilityChange? visible = null;
        tracker.OnEntityVisible += v => visible = v;

        tracker.Tick();

        Assert.NotNull(visible);
        Assert.Equal("player1", visible.Value.ObserverClientId);
        Assert.Same(npc, visible.Value.Target);
    }

    [Fact]
    public void Tick_ShouldFireOnEntityInvisible_WhenEntityLeavesRange()
    {
        var player = new TestWorldObj2D(0, 0, clientId: "player1");
        var npc = new TestWorldObj2D(100, 0);
        var world = CreateMockWorld(player, npc);
        var (tracker, _) = SetupTracker(world, 1000f);

        tracker.Tick();

        // Move npc far away — create new mock with updated position
        var farNpc = new TestWorldObj2D(5000, 5000) { InstanceId = npc.InstanceId };
        world.Setup(w => w.FindAllObjects<IWorldObject2D>()).Returns(new[] { player, farNpc }.AsEnumerable());

        VisibilityChange? invisible = null;
        tracker.OnEntityInvisible += v => invisible = v;

        tracker.Tick();

        Assert.NotNull(invisible);
        Assert.Equal("player1", invisible.Value.ObserverClientId);
    }

    [Fact]
    public void Tick_ShouldNotFireVisible_ForSelf()
    {
        var player = new TestWorldObj2D(0, 0, clientId: "player1");
        var world = CreateMockWorld(player);
        var (tracker, _) = SetupTracker(world);

        VisibilityChange? visible = null;
        tracker.OnEntityVisible += v => visible = v;

        tracker.Tick();

        Assert.Null(visible);
    }

    [Fact]
    public void Tick_ShouldNotFireDuplicate_WhenEntityStaysInRange()
    {
        var player = new TestWorldObj2D(0, 0, clientId: "player1");
        var npc = new TestWorldObj2D(100, 0);
        var world = CreateMockWorld(player, npc);
        var (tracker, _) = SetupTracker(world);

        int visibleCount = 0;
        tracker.OnEntityVisible += _ => visibleCount++;

        tracker.Tick();
        tracker.Tick();

        Assert.Equal(1, visibleCount);
    }

    [Fact]
    public void GetVisibleEntities_ShouldReturnCurrentlyVisible()
    {
        var player = new TestWorldObj2D(0, 0, clientId: "player1");
        var npc1 = new TestWorldObj2D(100, 0);
        var npc2 = new TestWorldObj2D(200, 0);
        var world = CreateMockWorld(player, npc1, npc2);
        var (tracker, _) = SetupTracker(world);

        tracker.Tick();

        var visible = tracker.GetVisibleEntities("player1");
        Assert.NotNull(visible);
        Assert.Contains(npc1.InstanceId, visible);
        Assert.Contains(npc2.InstanceId, visible);
    }

    [Fact]
    public void GetObserversOf_ShouldReturnAllObservers()
    {
        var p1 = new TestWorldObj2D(0, 0, clientId: "player1");
        var p2 = new TestWorldObj2D(10, 0, clientId: "player2");
        var npc = new TestWorldObj2D(50, 0);
        var world = CreateMockWorld(p1, p2, npc);
        var (tracker, _) = SetupTracker(world);

        tracker.Tick();

        var observers = tracker.GetObserversOf(npc.InstanceId).ToList();
        Assert.Contains("player1", observers);
        Assert.Contains("player2", observers);
    }

    [Fact]
    public void ViewRange_ShouldRespectConfiguredDistance()
    {
        var player = new TestWorldObj2D(0, 0, clientId: "player1");
        var farNpc = new TestWorldObj2D(200, 0);
        var world = CreateMockWorld(player, farNpc);
        var (tracker, _) = SetupTracker(world, 100f);

        VisibilityChange? visible = null;
        tracker.OnEntityVisible += v => visible = v;

        tracker.Tick();

        Assert.Null(visible);
    }

    [Fact]
    public void RefreshObserver_ShouldAllowRefire()
    {
        var player = new TestWorldObj2D(0, 0, clientId: "player1");
        var npc = new TestWorldObj2D(100, 0);
        var world = CreateMockWorld(player, npc);
        var (tracker, _) = SetupTracker(world);

        int visibleCount = 0;
        tracker.OnEntityVisible += _ => visibleCount++;

        tracker.Tick();
        Assert.Equal(1, visibleCount);

        tracker.RefreshObserver("player1");
        tracker.Tick();
        Assert.Equal(2, visibleCount);
    }

    [Fact]
    public void RemoveObserver_ShouldFireInvisibleForAllVisible()
    {
        var player = new TestWorldObj2D(0, 0, clientId: "player1");
        var npc = new TestWorldObj2D(100, 0);
        var world = CreateMockWorld(player, npc);
        var (tracker, _) = SetupTracker(world);

        tracker.Tick();

        int invisibleCount = 0;
        tracker.OnEntityInvisible += _ => invisibleCount++;

        tracker.RemoveObserver("player1");

        Assert.True(invisibleCount > 0);
    }

    [Fact]
    public void RemoveObserver_ShouldCleanupState()
    {
        var player = new TestWorldObj2D(0, 0, clientId: "player1");
        var npc = new TestWorldObj2D(100, 0);
        var world = CreateMockWorld(player, npc);
        var (tracker, _) = SetupTracker(world);

        tracker.Tick();
        tracker.RemoveObserver("player1");

        Assert.Null(tracker.GetVisibleEntities("player1"));
    }
}
