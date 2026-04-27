using Altruist.Physx.ThreeD;

namespace Tests.Physx.Heightmap;

public sealed class RawHeightmapLoaderTests
{
    private const int Width = 521;
    private const int Height = 651;
    private const float WorldWidth = 1024f;
    private const float WorldDepth = 1280f;
    private const float HeightScale = 0.005f;

    [Fact]
    public void LoadUInt16Heightmap_ShouldLoadJinnoFixtureWithExpectedDimensions()
    {
        var loader = new RawHeightmapLoader();

        var heightfield = loader.LoadUInt16Heightmap(FixturePath(), Options(flipZ: true));

        Assert.Equal(Width, heightfield.Width);
        Assert.Equal(Height, heightfield.Height);
        Assert.Equal(WorldWidth / (Width - 1), heightfield.CellSizeX, precision: 6);
        Assert.Equal(WorldDepth / (Height - 1), heightfield.CellSizeZ, precision: 6);
        Assert.Equal(1f, heightfield.HeightScale);
    }

    [Fact]
    public void LoadUInt16Heightmap_ShouldFlipZAndScaleSamplesLikeUnityTerrainImport()
    {
        var loader = new RawHeightmapLoader();
        var heightfield = loader.LoadUInt16Heightmap(FixturePath(), Options(flipZ: true));

        float height = heightfield.SampleHeight(localX: 296f, localZ: 903f);

        Assert.InRange(height, 184.70f, 184.81f);
    }

    [Fact]
    public void LoadUInt16Heightmap_ShouldExposeDifferentHeightWhenZIsNotFlipped()
    {
        var loader = new RawHeightmapLoader();
        var flipped = loader.LoadUInt16Heightmap(FixturePath(), Options(flipZ: true));
        var unflipped = loader.LoadUInt16Heightmap(FixturePath(), Options(flipZ: false));

        float flippedHeight = flipped.SampleHeight(localX: 296f, localZ: 903f);
        float unflippedHeight = unflipped.SampleHeight(localX: 296f, localZ: 903f);

        Assert.InRange(flippedHeight, 184.70f, 184.81f);
        Assert.InRange(unflippedHeight, 166.30f, 166.42f);
        Assert.True(flippedHeight - unflippedHeight > 18f);
    }

    private static RawHeightmapLoadOptions Options(bool flipZ) => new()
    {
        Width = Width,
        Height = Height,
        CellSizeX = WorldWidth / (Width - 1),
        CellSizeZ = WorldDepth / (Height - 1),
        HeightScale = HeightScale,
        FlipZ = flipZ,
    };

    private static string FixturePath()
        => Path.Combine(AppContext.BaseDirectory, "Resources", "Heightmaps", "jinno_heightmap_521x651_u16.raw");
}
