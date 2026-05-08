using Altruist.Networking;

namespace Tests.Framework.Networking;

public class SyncChangeMapTests
{
    [Synchronized]
    public class MapTestEntity : ISynchronizedEntity
    {
        public string ClientId { get; set; } = "";
        [Synced(0)] public int Hp { get; set; } = 100;
        [Synced(1, SyncAlways: true)] public string Name { get; set; } = "test";
    }

    [Fact]
    public void GetSyncChanges_ReturnsChanges_WhenPropertyChanged()
    {
        var entity = new MapTestEntity { ClientId = Guid.NewGuid().ToString("N") };
        // First call — establishes baseline
        using (var first = Synchronization.GetSyncChanges(entity, entity.ClientId, 1))
        {
            Assert.True(first.HasChanges); // First sync always has changes
        }

        entity.Hp = 50;
        using var changes = Synchronization.GetSyncChanges(entity, entity.ClientId, 2);

        Assert.True(changes.HasChanges);
        Assert.Contains("Hp", changes.Data.Keys);
        Assert.Equal(50, changes.Data["Hp"]);
    }

    [Fact]
    public void GetSyncChanges_NoChanges_HasChangesFalse()
    {
        var entity = new MapTestEntity { ClientId = Guid.NewGuid().ToString("N") };
        using (var first = Synchronization.GetSyncChanges(entity, entity.ClientId, 1)) { }

        using var changes = Synchronization.GetSyncChanges(entity, entity.ClientId, 2);

        // No deltas → no packet. SyncAlways (Name) doesn't fire by itself —
        // it rides along on packets that already carry a real delta. See
        // Synchronization.GetChangedData lines 245-261 for the design rationale
        // (per-tick SyncAlways packets would scale to 1000s × 25Hz = catastrophic).
        Assert.False(changes.HasChanges);
        Assert.DoesNotContain("Name", changes.Data.Keys);
    }

    [Fact]
    public void GetSyncChanges_Dispose_ReturnsMasks()
    {
        var entity = new MapTestEntity { ClientId = Guid.NewGuid().ToString("N") };
        var changes = Synchronization.GetSyncChanges(entity, entity.ClientId, 1);

        Assert.NotNull(changes.Masks);
        changes.Dispose();
        Assert.Null(changes.Masks); // Returned to pool
    }

    [Fact]
    public void GetSyncChanges_DataReusedAcrossCalls()
    {
        var entity = new MapTestEntity { ClientId = Guid.NewGuid().ToString("N") };

        Dictionary<string, object?>? firstData;
        using (var first = Synchronization.GetSyncChanges(entity, entity.ClientId, 1))
        {
            firstData = first.Data;
        }

        using var second = Synchronization.GetSyncChanges(entity, entity.ClientId, 2);

        // Same dictionary instance reused (pooled per entity)
        Assert.Same(firstData, second.Data);
    }
}
