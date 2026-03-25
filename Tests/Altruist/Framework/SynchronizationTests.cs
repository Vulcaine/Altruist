using Altruist.Networking;

namespace Tests.Framework.Networking;

public class TestSyncEntity : ISynchronizedEntity
{
    public string ClientId { get; set; } = "";

    [Synced(0)]
    public int Value { get; set; } = 42;

    [Synced(1, SyncAlways: true)]
    public string Name { get; set; } = "test";

    [Synced(2, oneTime: true)]
    public int InitFlag { get; set; } = 1;

    [Synced(3, syncFrequency: 5)]
    public int Throttled { get; set; } = 10;

    [Synced(4)]
    public int[] Scores { get; set; } = [1, 2, 3];
}

public class DerivedSyncEntity : TestSyncEntity
{
    [Synced(10)]
    public int ExtraField { get; set; } = 99;
}

public class SynchronizationGetChangedDataTests
{
    private static string UniqueId() => Guid.NewGuid().ToString("N");

    [Fact]
    public void FirstSync_ShouldIncludeAllProperties()
    {
        var entity = new TestSyncEntity { ClientId = UniqueId() };

        var (masks, _, data) = Synchronization.GetChangedData(entity, entity.ClientId, 1);

        Assert.True(data.Count >= 4); // Value, Name, InitFlag, Throttled at minimum
        Assert.Contains("Value", data.Keys);
        Assert.Contains("Name", data.Keys);
    }

    [Fact]
    public void UnchangedProperties_ShouldNotBeIncluded_ExceptSyncAlways()
    {
        var entity = new TestSyncEntity { ClientId = UniqueId() };

        // First sync
        Synchronization.GetChangedData(entity, entity.ClientId, 1);

        // Second sync with no changes — only SyncAlways should appear
        var (_, _, data) = Synchronization.GetChangedData(entity, entity.ClientId, 2);

        Assert.Contains("Name", data.Keys); // SyncAlways
        // Value should NOT be included (unchanged, not SyncAlways)
        Assert.DoesNotContain("Value", data.Keys);
    }

    [Fact]
    public void ChangedInt_ShouldBeDetected()
    {
        var entity = new TestSyncEntity { ClientId = UniqueId() };
        Synchronization.GetChangedData(entity, entity.ClientId, 1);

        entity.Value = 99;
        var (_, _, data) = Synchronization.GetChangedData(entity, entity.ClientId, 2);

        Assert.Contains("Value", data.Keys);
        Assert.Equal(99, data["Value"]);
    }

    [Fact]
    public void ChangedString_ShouldBeDetected()
    {
        var entity = new TestSyncEntity { ClientId = UniqueId() };
        Synchronization.GetChangedData(entity, entity.ClientId, 1);

        entity.Name = "changed";
        var (_, _, data) = Synchronization.GetChangedData(entity, entity.ClientId, 2);

        Assert.Contains("Name", data.Keys);
        Assert.Equal("changed", data["Name"]);
    }

    [Fact]
    public void ChangedArray_ShouldBeDetected()
    {
        var entity = new TestSyncEntity { ClientId = UniqueId() };
        Synchronization.GetChangedData(entity, entity.ClientId, 1);

        entity.Scores = [9, 8, 7];
        var (_, _, data) = Synchronization.GetChangedData(entity, entity.ClientId, 2);

        Assert.Contains("Scores", data.Keys);
    }

    [Fact]
    public void SyncAlways_ShouldAlwaysBeIncluded()
    {
        var entity = new TestSyncEntity { ClientId = UniqueId() };
        Synchronization.GetChangedData(entity, entity.ClientId, 1);

        // No changes — SyncAlways Name should still be included
        var (_, _, data) = Synchronization.GetChangedData(entity, entity.ClientId, 2);

        Assert.Contains("Name", data.Keys);
        Assert.Equal("test", data["Name"]);
    }

    [Fact]
    public void TickFrequency_ShouldSyncOnMatchingTick()
    {
        var entity = new TestSyncEntity { ClientId = UniqueId() };
        // First sync at tick 5 (divisible by 5) — should include Throttled
        var (_, _, data5) = Synchronization.GetChangedData(entity, entity.ClientId, 5);
        Assert.Contains("Throttled", data5.Keys);

        // Change and sync at tick 7 (not divisible by 5)
        entity.Throttled = 99;
        var (_, _, data7) = Synchronization.GetChangedData(entity, entity.ClientId, 7);
        Assert.DoesNotContain("Throttled", data7.Keys);

        // Sync at tick 10 (divisible by 5) — should include
        var (_, _, data10) = Synchronization.GetChangedData(entity, entity.ClientId, 10);
        Assert.Contains("Throttled", data10.Keys);
    }

    [Fact]
    public void ForceAllChanged_ShouldIncludeAllProperties()
    {
        var entity = new TestSyncEntity { ClientId = UniqueId() };
        Synchronization.GetChangedData(entity, entity.ClientId, 1);

        // No changes, but force all
        var (_, _, data) = Synchronization.GetChangedData(entity, entity.ClientId, 2, forceAllAsChanged: true);

        Assert.Contains("Value", data.Keys);
        Assert.Contains("Name", data.Keys);
        Assert.Contains("Scores", data.Keys);
    }

    [Fact]
    public void Bitmask_ShouldSetCorrectBits()
    {
        var entity = new TestSyncEntity { ClientId = UniqueId() };

        var (masks, _, data) = Synchronization.GetChangedData(entity, entity.ClientId, 1);

        // All properties should be set on first sync
        Assert.True(masks.Length > 0);
        Assert.True(masks[0] != 0); // At least some bits set
    }

    [Fact]
    public void DerivedEntity_ShouldIncludeBaseAndLocalProperties()
    {
        var entity = new DerivedSyncEntity { ClientId = UniqueId(), ExtraField = 77 };

        var (_, _, data) = Synchronization.GetChangedData(entity, entity.ClientId, 1);

        Assert.Contains("Value", data.Keys);     // base
        Assert.Contains("Name", data.Keys);       // base SyncAlways
        Assert.Contains("ExtraField", data.Keys); // derived
        Assert.Equal(77, data["ExtraField"]);
    }

    [Fact]
    public void NullToValue_ShouldBeDetectedAsChange()
    {
        var entity = new TestSyncEntity { ClientId = UniqueId(), Scores = null! };
        Synchronization.GetChangedData(entity, entity.ClientId, 1);

        entity.Scores = [1, 2, 3];
        var (_, _, data) = Synchronization.GetChangedData(entity, entity.ClientId, 2);

        Assert.Contains("Scores", data.Keys);
    }
}
