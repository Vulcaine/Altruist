using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Moq;

namespace Tests.Gaming.World.ThreeD;

/// <summary>
/// Zone activation spawns thousands of entities on one thread while the visibility tick
/// diffs every observer's visible set on another. Both touch the same per-observer set,
/// so the tick must never see "Collection was modified" and an entity must never be
/// reported invisible twice.
/// </summary>
public class VisibilityTracker3DConcurrencyTests
{
    private static Mock<IGameWorldManager3D> CreateMockWorld()
    {
        var world = new Mock<IGameWorldManager3D>();
        var index = new Mock<IWorldIndex3D>();
        index.Setup(i => i.Index).Returns(0);
        index.Setup(i => i.Name).Returns("test");
        world.Setup(w => w.Index).Returns(index.Object);
        return world;
    }

    private static WorldSnapshot Snapshot(Mock<IGameWorldManager3D> world, IReadOnlyList<IWorldObject3D> objects)
    {
        var list = objects.Cast<ITypelessWorldObject>().ToList();
        var lookup = objects.ToDictionary(o => o.InstanceId, o => (ITypelessWorldObject)o);
        return new WorldSnapshot(world.Object.Index.Index, list, lookup);
    }

    [Fact]
    public async Task tick_survives_spawns_and_destroys_raised_concurrently()
    {
        var tracker = new VisibilityTracker3D(500f);
        var organizer = new Mock<IGameWorldOrganizer3D>();
        var world = CreateMockWorld();
        organizer.Setup(o => o.GetWorld(0)).Returns(world.Object);
        organizer.Setup(o => o.GetAllWorlds()).Returns([world.Object]);
        tracker.SetOrganizer(organizer.Object);

        var player = new TestWorldObj(0, 0, clientId: "p1");
        tracker.Observe(player);

        var resident = Enumerable.Range(0, 400).Select(i => (IWorldObject3D)new TestWorldObj(i % 100, 0)).ToList();
        var full = Snapshot(world, [player, .. resident]);
        var half = Snapshot(world, [player, .. resident.Take(200)]);

        var invisibleCounts = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        tracker.OnEntityInvisible += c => invisibleCounts.AddOrUpdate(c.Target.InstanceId, 1, (_, n) => n + 1);

        using var stop = new CancellationTokenSource();
        var spawner = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                var spawned = new TestWorldObj(1, 1);
                world.Raise(w => w.OnObjectCreated += null, (IWorldObject3D)spawned);
                world.Raise(w => w.OnObjectDestroyed += null, (IWorldObject3D)spawned);
            }
        });

        try
        {
            for (int i = 0; i < 300; i++)
                tracker.Tick([i % 2 == 0 ? full : half]);
        }
        finally
        {
            stop.Cancel();
            await spawner;
        }

        Assert.All(invisibleCounts.Where(kv => !resident.Any(r => r.InstanceId == kv.Key)),
            kv => Assert.Equal(1, kv.Value));
    }
}
