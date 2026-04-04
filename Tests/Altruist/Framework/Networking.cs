namespace Altruist.Networking;

public class BaseEntity
{
    [Synced(0)]
    public int A { get; set; }

    [Synced(1, SyncAlways: true)]
    public int B { get; set; }
}

public class DerivedEntity : BaseEntity
{
    [Synced(0)]
    public int C { get; set; }

    [Synced(1)]
    public int D { get; set; }
}

public class SyncMetadataHelperTests
{
    [Fact]
    public void Metadata_ShouldIncludeBaseAndDerivedProperties_WithCorrectBitIndices()
    {
        var metadata = SyncMetadataHelper.GetSyncMetadata(typeof(DerivedEntity));
        var props = metadata.Properties;
        var count = metadata.Count;

        Assert.Equal(4, count);

        var expected = new Dictionary<string, int>
        {
            ["A"] = 0,
            ["B"] = 1,
            ["C"] = 2, // base max index = 1 → +1 = 2
            ["D"] = 3  // BitIndex 1 + baseMax 1 + 1 = 3
        };

        foreach (var p in props)
        {
            Assert.True(expected.ContainsKey(p.Name), $"Unexpected property: {p.Name}");
            Assert.Equal(expected[p.Name], p.BitIndex);
        }

        Assert.True(props.First(p => p.Name == "B").SyncAlways);
    }

    [Fact]
    public void Metadata_CachesResultAcrossCalls()
    {
        var meta1 = SyncMetadataHelper.GetSyncMetadata(typeof(DerivedEntity));
        var meta2 = SyncMetadataHelper.GetSyncMetadata(typeof(DerivedEntity));

        // Should return same instance due to caching
        Assert.Same(meta1.Properties, meta2.Properties);
    }
}
