using Altruist.Dashboard;

namespace Tests.Gaming.Dashboard;

public sealed class DashboardGizmoRegistryTests
{
    [Fact]
    public void Register_AddsSnapshotAndDirtyChange()
    {
        var registry = new DashboardGizmoRegistry();

        registry.Register(Gizmo("g1", worldIndex: 0));

        Assert.Single(registry.GetSnapshot(0));
        var changes = registry.DrainChanges(0);
        Assert.Single(changes.Gizmos);
        Assert.Empty(changes.RemovedGizmoIds);
    }

    [Fact]
    public void Update_MarksExistingGizmoDirtyWithoutChangingShape()
    {
        var registry = new DashboardGizmoRegistry();
        registry.Register(Gizmo("g1", worldIndex: 0));
        registry.DrainChanges(0);

        Assert.True(registry.Update("g1"));

        var changes = registry.DrainChanges(0);
        Assert.Single(changes.Gizmos);
        Assert.Equal("g1", changes.Gizmos[0].Id);
        Assert.Empty(registry.DrainChanges(0).Gizmos);
    }

    [Fact]
    public void UpdateConfigure_ChangesGizmoAndMarksDirty()
    {
        var registry = new DashboardGizmoRegistry();
        registry.Register(Gizmo("g1", worldIndex: 0));
        registry.DrainChanges(0);

        registry.Update("g1", gizmo =>
        {
            gizmo.Label = "changed";
            gizmo.Position = new Vector3Dto { X = 3, Y = 4, Z = 5 };
        });

        var changed = Assert.Single(registry.DrainChanges(0).Gizmos);
        Assert.Equal("changed", changed.Label);
        Assert.Equal(3, changed.Position.X);
    }

    [Fact]
    public void Remove_DrainsRemovedId()
    {
        var registry = new DashboardGizmoRegistry();
        registry.Register(Gizmo("g1", worldIndex: 0));
        registry.DrainChanges(0);

        Assert.True(registry.Remove("g1"));

        var changes = registry.DrainChanges(0);
        Assert.Empty(changes.Gizmos);
        Assert.Equal(["g1"], changes.RemovedGizmoIds);
    }

    [Fact]
    public void TtlExpiredGizmo_IsRemovedAndDrained()
    {
        var registry = new DashboardGizmoRegistry();
        var expired = Gizmo("g1", worldIndex: 0);
        expired.CreatedAtUtc = DateTime.UtcNow.AddSeconds(-20);
        expired.TtlSeconds = 1;
        registry.Register(expired);

        Assert.Empty(registry.GetSnapshot(0));
        Assert.Equal(["g1"], registry.DrainChanges(0).RemovedGizmoIds);
    }

    [Fact]
    public void Snapshot_IsScopedByWorld()
    {
        var registry = new DashboardGizmoRegistry();
        registry.Register(Gizmo("g1", worldIndex: 0));
        registry.Register(Gizmo("g2", worldIndex: 1));

        Assert.Equal("g1", Assert.Single(registry.GetSnapshot(0)).Id);
        Assert.Equal("g2", Assert.Single(registry.GetSnapshot(1)).Id);
    }

    private static DashboardGizmo Gizmo(string id, int worldIndex)
        => new()
        {
            Id = id,
            WorldIndex = worldIndex,
            Category = "combat",
            Source = "test",
            Type = "sphere",
            Label = id,
            Position = new Vector3Dto { X = 1, Y = 2, Z = 3 },
            Radius = 1,
        };
}
