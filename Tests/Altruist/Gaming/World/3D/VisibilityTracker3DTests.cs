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

    private WorldSnapshot CreateSnapshot(IGameWorldManager3D world, params IWorldObject3D[] objects)
    {
        var list = objects.ToList();
        var lookup = objects.ToDictionary(o => o.InstanceId, o => o);
        return new WorldSnapshot(world, list, lookup);
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

    [Fact]
    public void Tick_ShouldFireOnEntityVisible_WhenEntityEntersRange()
    {
        var tracker = CreateTracker(1000f);
        var world = CreateMockWorld();
        var organizer = new Mock<IGameWorldOrganizer3D>();
        tracker.SetOrganizer(organizer.Object);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc = new TestWorldObj(100, 0); // within 1000 range
        var snapshot = CreateSnapshot(world.Object, player, npc);

        VisibilityChange? visible = null;
        tracker.OnEntityVisible += v => visible = v;

        tracker.Tick([snapshot]);

        Assert.NotNull(visible);
        Assert.Equal("player1", visible.Value.ObserverClientId);
        Assert.Same(npc, visible.Value.Target);
    }

    [Fact]
    public void Tick_ShouldFireOnEntityInvisible_WhenEntityLeavesRange()
    {
        var tracker = CreateTracker(1000f);
        var world = CreateMockWorld();
        var organizer = new Mock<IGameWorldOrganizer3D>();
        tracker.SetOrganizer(organizer.Object);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc = new TestWorldObj(100, 0);

        // First tick — npc visible
        tracker.Tick([CreateSnapshot(world.Object, player, npc)]);

        // Move npc far away
        npc.Transform = Transform3D.From(new Vector3(5000, 5000, 0), Quaternion.Identity, Vector3.One);

        VisibilityChange? invisible = null;
        tracker.OnEntityInvisible += v => invisible = v;

        tracker.Tick([CreateSnapshot(world.Object, player, npc)]);

        Assert.NotNull(invisible);
        Assert.Equal("player1", invisible.Value.ObserverClientId);
    }

    [Fact]
    public void Tick_ShouldNotFireVisible_ForSelf()
    {
        var tracker = CreateTracker(5000f);
        var world = CreateMockWorld();
        var organizer = new Mock<IGameWorldOrganizer3D>();
        tracker.SetOrganizer(organizer.Object);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var snapshot = CreateSnapshot(world.Object, player);

        VisibilityChange? visible = null;
        tracker.OnEntityVisible += v => visible = v;

        tracker.Tick([snapshot]);

        Assert.Null(visible);
    }

    [Fact]
    public void Tick_ShouldNotFireDuplicate_WhenEntityStaysInRange()
    {
        var tracker = CreateTracker(5000f);
        var world = CreateMockWorld();
        var organizer = new Mock<IGameWorldOrganizer3D>();
        tracker.SetOrganizer(organizer.Object);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc = new TestWorldObj(100, 0);

        int visibleCount = 0;
        tracker.OnEntityVisible += _ => visibleCount++;

        tracker.Tick([CreateSnapshot(world.Object, player, npc)]);
        tracker.Tick([CreateSnapshot(world.Object, player, npc)]);

        Assert.Equal(1, visibleCount); // Only fires once
    }

    [Fact]
    public void GetVisibleEntities_ShouldReturnCurrentlyVisible()
    {
        var tracker = CreateTracker(5000f);
        var world = CreateMockWorld();
        var organizer = new Mock<IGameWorldOrganizer3D>();
        tracker.SetOrganizer(organizer.Object);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc1 = new TestWorldObj(100, 0);
        var npc2 = new TestWorldObj(200, 0);

        tracker.Tick([CreateSnapshot(world.Object, player, npc1, npc2)]);

        var visible = tracker.GetVisibleEntities("player1");
        Assert.NotNull(visible);
        Assert.Contains(npc1.InstanceId, visible);
        Assert.Contains(npc2.InstanceId, visible);
    }

    [Fact]
    public void GetObserversOf_ShouldReturnAllObservers()
    {
        var tracker = CreateTracker(5000f);
        var world = CreateMockWorld();
        var organizer = new Mock<IGameWorldOrganizer3D>();
        tracker.SetOrganizer(organizer.Object);

        var p1 = new TestWorldObj(0, 0, clientId: "player1");
        var p2 = new TestWorldObj(10, 0, clientId: "player2");
        var npc = new TestWorldObj(50, 0);

        tracker.Tick([CreateSnapshot(world.Object, p1, p2, npc)]);

        var observers = tracker.GetObserversOf(npc.InstanceId).ToList();
        Assert.Contains("player1", observers);
        Assert.Contains("player2", observers);
    }

    [Fact]
    public void ViewRange_ShouldRespectConfiguredDistance()
    {
        var tracker = CreateTracker(100f); // Very short range
        var world = CreateMockWorld();
        var organizer = new Mock<IGameWorldOrganizer3D>();
        tracker.SetOrganizer(organizer.Object);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var farNpc = new TestWorldObj(200, 0); // Outside 100 range

        VisibilityChange? visible = null;
        tracker.OnEntityVisible += v => visible = v;

        tracker.Tick([CreateSnapshot(world.Object, player, farNpc)]);

        Assert.Null(visible);
    }

    [Fact]
    public void RefreshObserver_ShouldAllowRefire()
    {
        var tracker = CreateTracker(5000f);
        var world = CreateMockWorld();
        var organizer = new Mock<IGameWorldOrganizer3D>();
        tracker.SetOrganizer(organizer.Object);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc = new TestWorldObj(100, 0);

        int visibleCount = 0;
        tracker.OnEntityVisible += _ => visibleCount++;

        tracker.Tick([CreateSnapshot(world.Object, player, npc)]);
        Assert.Equal(1, visibleCount);

        tracker.RefreshObserver("player1");
        tracker.Tick([CreateSnapshot(world.Object, player, npc)]);
        Assert.Equal(2, visibleCount); // Fires again after refresh
    }

    [Fact]
    public void RemoveObserver_ShouldFireInvisibleForAllVisible()
    {
        var tracker = CreateTracker(5000f);
        var world = CreateMockWorld();
        var organizer = new Mock<IGameWorldOrganizer3D>();
        tracker.SetOrganizer(organizer.Object);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc = new TestWorldObj(100, 0);

        // First tick registers visibility
        tracker.Tick([CreateSnapshot(world.Object, player, npc)]);

        // Setup GetCachedSnapshot to return the SAME objects for RemoveObserver lookup
        var list = new List<IWorldObject3D> { player, npc };
        var lookup = list.ToDictionary(o => o.InstanceId, o => o);
        world.Setup(w => w.GetCachedSnapshot())
            .Returns((list as IReadOnlyList<IWorldObject3D>, lookup as IReadOnlyDictionary<string, IWorldObject3D>));
        organizer.Setup(o => o.GetAllWorlds()).Returns([world.Object]);

        int invisibleCount = 0;
        tracker.OnEntityInvisible += _ => invisibleCount++;

        tracker.RemoveObserver("player1");

        Assert.True(invisibleCount > 0);
    }

    [Fact]
    public void RemoveObserver_ShouldCleanupState()
    {
        var tracker = CreateTracker(5000f);
        var world = CreateMockWorld();
        var organizer = new Mock<IGameWorldOrganizer3D>();
        organizer.Setup(o => o.GetAllWorlds()).Returns([world.Object]);
        world.Setup(w => w.GetCachedSnapshot()).Returns(() =>
        {
            var list = new List<IWorldObject3D>();
            var lookup = new Dictionary<string, IWorldObject3D>();
            return (list, (IReadOnlyDictionary<string, IWorldObject3D>)lookup);
        });
        tracker.SetOrganizer(organizer.Object);

        var player = new TestWorldObj(0, 0, clientId: "player1");
        var npc = new TestWorldObj(100, 0);
        tracker.Tick([CreateSnapshot(world.Object, player, npc)]);

        tracker.RemoveObserver("player1");

        Assert.Null(tracker.GetVisibleEntities("player1"));
    }
}
