using Altruist.Gaming;
using Altruist.Gaming.TwoD;
using Moq;

namespace Tests.Gaming.World.TwoD;

public class VisibilityTracker2DMultiWorldTests
{
    private static Mock<IGameWorldManager2D> World(int index, params IWorldObject2D[] objects)
    {
        var world = new Mock<IGameWorldManager2D>();
        var idx = new Mock<IWorldIndex2D>();
        idx.Setup(i => i.Index).Returns(index);
        world.Setup(w => w.Index).Returns(idx.Object);
        world.Setup(w => w.FindAllObjects<IWorldObject2D>()).Returns(objects.AsEnumerable());
        return world;
    }

    [Fact]
    public void Observer_in_the_second_world_keeps_observing()
    {
        var player = new TestWorldObj2D(0, 0, clientId: "p1");
        var npc = new TestWorldObj2D(10, 0);
        var organizer = new Mock<IGameWorldOrganizer2D>();
        organizer.Setup(o => o.GetAllWorlds()).Returns([World(0).Object, World(1, player, npc).Object]);
        var tracker = new VisibilityTracker2D(1000f);
        tracker.SetOrganizer(organizer.Object);
        Assert.True(tracker.Observe(player));
        var seen = new List<VisibilityChange>();
        tracker.OnEntityVisible += seen.Add;

        tracker.Tick();
        tracker.Tick();

        Assert.Single(seen);
        Assert.Equal(1, seen[0].WorldIndex);
        Assert.Contains(player, tracker.GetObservers());
    }
}
