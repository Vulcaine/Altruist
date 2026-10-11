using Altruist.Dashboard;

using Moq;

namespace Tests.Gaming.Dashboard;

/// <summary>The live world feed: a viewer starts from the whole world, then gets exactly what changed.</summary>
public class DashboardWorldFeedTests
{
    /// <summary>A world whose objects the test moves by hand.</summary>
    private sealed class FakeSource : IDashboardWorldSource
    {
        public readonly Dictionary<string, (float X, float Y)> Positions = new();
        public int Dimensions => 2;

        public IReadOnlyList<WorldSummaryDto> Worlds() => [new WorldSummaryDto { Index = 0, Name = "w", Dimensions = 2, ObjectCount = Positions.Count }];
        public bool Exists(int worldIndex) => worldIndex == 0;
        public IEnumerable<WorldPartitionObjectsDto> Partitions(int worldIndex) => [new WorldPartitionObjectsDto { Objects = Objects(worldIndex).ToList() }];

        public IEnumerable<WorldObjectDto> Objects(int worldIndex) => Positions.Keys.Select(id => Object(worldIndex, id)!).ToList();

        public IEnumerable<DashboardObjectPoseDto> LiveObjects(int worldIndex) => Positions
            .Select(kv => new DashboardObjectPoseDto { InstanceId = kv.Key, Position = new Vector3Dto { X = kv.Value.X, Y = kv.Value.Y } })
            .ToList();

        public WorldObjectDto? Object(int worldIndex, string instanceId) => Positions.TryGetValue(instanceId, out var p)
            ? new WorldObjectDto { InstanceId = instanceId, Transform = new TransformDto { Position = new Vector3Dto { X = p.X, Y = p.Y }, Size = new(), Scale = new() } }
            : null;

        public IEnumerable<DashboardGizmo> SourceGizmos(int worldIndex) => [];
    }

    private static Mock<IDashboardGizmoRegistry> Gizmos()
    {
        var gizmos = new Mock<IDashboardGizmoRegistry>();
        gizmos.Setup(g => g.DrainChanges(It.IsAny<int>())).Returns(new DashboardGizmoChangeSet());
        gizmos.Setup(g => g.GetSnapshot(It.IsAny<int>())).Returns([]);
        return gizmos;
    }

    private static List<DashboardWorldDelta> Read(DashboardWorldSubscription subscription)
    {
        var deltas = new List<DashboardWorldDelta>();
        while (subscription.Deltas.TryRead(out var d))
            deltas.Add(d);
        return deltas;
    }

    [Fact]
    public void A_viewer_starts_from_the_whole_world_then_gets_moves_arrivals_and_departures()
    {
        var source = new FakeSource();
        source.Positions["a"] = (1, 1);
        source.Positions["b"] = (2, 2);
        var feed = new DashboardWorldFeed(Gizmos().Object, source);
        using var viewer = feed.Subscribe(0);

        feed.Publish();
        var baseline = Assert.Single(Read(viewer));
        Assert.True(baseline.Reset);
        Assert.Equal(["a", "b"], baseline.Added.Select(o => o.InstanceId).Order());

        source.Positions["a"] = (5, 1);
        source.Positions.Remove("b");
        source.Positions["c"] = (3, 3);
        feed.Publish();

        var delta = Assert.Single(Read(viewer));
        Assert.False(delta.Reset);
        Assert.Equal("a", Assert.Single(delta.Moved).InstanceId);
        Assert.Equal(5, delta.Moved[0].Position.X);
        Assert.Equal("c", Assert.Single(delta.Added).InstanceId);
        Assert.Equal("b", Assert.Single(delta.Removed));
    }

    [Fact]
    public void Nothing_is_sent_while_nothing_changes()
    {
        var source = new FakeSource();
        source.Positions["a"] = (1, 1);
        var feed = new DashboardWorldFeed(Gizmos().Object, source);
        using var viewer = feed.Subscribe(0);
        feed.Publish();
        Read(viewer);

        feed.Publish();
        feed.Publish();

        Assert.Empty(Read(viewer));
    }

    [Fact]
    public void A_second_viewer_gets_its_own_baseline_and_the_first_is_not_reset()
    {
        var source = new FakeSource();
        source.Positions["a"] = (1, 1);
        var feed = new DashboardWorldFeed(Gizmos().Object, source);
        using var first = feed.Subscribe(0);
        feed.Publish();
        Read(first);

        source.Positions["a"] = (2, 1);
        using var second = feed.Subscribe(0);
        feed.Publish();

        Assert.False(Assert.Single(Read(first)).Reset);
        var baseline = Assert.Single(Read(second));
        Assert.True(baseline.Reset);
        Assert.Equal(2, baseline.Added.Single().Transform.Position.X);
    }

    [Fact]
    public void A_viewer_that_falls_behind_is_given_the_whole_world_again()
    {
        var source = new FakeSource();
        source.Positions["a"] = (0, 0);
        var feed = new DashboardWorldFeed(Gizmos().Object, source);
        using var viewer = feed.Subscribe(0);
        feed.Publish();

        // The viewer reads nothing while the world keeps moving: its queue fills and deltas are dropped.
        for (var i = 1; i <= 80; i++)
        {
            source.Positions["a"] = (i, 0);
            feed.Publish();
        }
        Read(viewer);
        source.Positions["a"] = (100, 0);
        feed.Publish();

        var next = Assert.Single(Read(viewer));
        Assert.True(next.Reset);
        Assert.Equal(100, next.Added.Single().Transform.Position.X);
    }

    [Fact]
    public void Gizmo_changes_are_drained_even_when_nobody_watches()
    {
        var gizmos = Gizmos();
        var feed = new DashboardWorldFeed(gizmos.Object, new FakeSource());

        feed.Publish();

        gizmos.Verify(g => g.DrainChanges(0), Times.Once);
    }

    [Fact]
    public void A_viewer_that_left_gets_nothing_more()
    {
        var source = new FakeSource();
        source.Positions["a"] = (0, 0);
        var feed = new DashboardWorldFeed(Gizmos().Object, source);
        var viewer = feed.Subscribe(0);
        viewer.Dispose();

        feed.Publish();

        Assert.Equal(0, feed.ViewerCount);
        Assert.Empty(Read(viewer));
    }
}
