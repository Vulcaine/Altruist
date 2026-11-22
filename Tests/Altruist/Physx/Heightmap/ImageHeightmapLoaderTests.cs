using Altruist.Physx.ThreeD;

namespace Tests.Physx.Heightmap;

public sealed class ImageHeightmapLoaderTests
{
    // Independently encoded fixtures: 2 x 3, row-major samples include adjacent 16-bit values.
    private const string Png = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADEAAAAADMEV0eAAAAFklEQVR4nGNgYGBgZGD4D8QNDP//AwANFgOACmk8RAAAAABJRU5ErkJggg==";
    private const string Tiff = "SUkqAAgAAAAJAAABBAABAAAAAgAAAAEBBAABAAAAAwAAAAIBAwABAAAAEAAAAAMBAwABAAAAAQAAAAYBAwABAAAAAQAAABEBBAABAAAAegAAABUBAwABAAAAAQAAABYBBAABAAAAAwAAABcBBAABAAAADAAAAAAAAAAAAAEA/wAAAQCA//8=";
    private const string Jpeg = "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQEBAQH/wAALCAADAAIBAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEBAAA/AP6k/hx/wRs/4Je/8K88B/8AGFfwZ/5Ezwv/AMwzV/8AoB2P/UYr/9k=";
    private const string ColorPng = "iVBORw0KGgoAAAANSUhEUgAAAAIAAAADCAIAAAA2iEnWAAAAFUlEQVR4nGP4z8DAAMJQ4v//hoYGAEDXB3uqBvz2AAAAAElFTkSuQmCC";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PreservesEverySixteenBitSampleAndRowOrientation(bool tiff)
    {
        IHeightmapFormatLoader loader = tiff ? new TiffHeightmapLoader() : new PngHeightmapLoader();
        using var input = new MemoryStream(Convert.FromBase64String(tiff ? Tiff : Png));
        var result = loader.LoadHeightmap(input);
        Assert.Equal(2, result.Width);
        Assert.Equal(3, result.Height);
        ushort[] expected = [0, 1, 255, 256, 32768, 65535];
        for (int z = 0; z < 3; z++)
            for (int x = 0; x < 2; x++)
                Assert.Equal(expected[z * 2 + x] / 65535f, result.Heights[x, z]);
        Assert.Equal(1f, result.CellSizeX);
        Assert.Equal(1f, result.CellSizeZ);
        Assert.Equal(1f, result.HeightScale);
        Assert.True(input.CanRead);
    }

    [Fact]
    public void ColorUsesRec709Luminance()
    {
        using var input = new MemoryStream(Convert.FromBase64String(ColorPng));
        var result = new PngHeightmapLoader().LoadHeightmap(input);
        float[] expected = [0.2126f, 0.7152f, 0.0722f, 0f, 1f, 128f/255];
        for (int z = 0; z < 3; z++)
            for (int x = 0; x < 2; x++)
                Assert.InRange(Math.Abs(result.Heights[x,z]-expected[z*2+x]),0f,1f/65535);
    }

    [Fact]
    public void JpegPreservesEightBitNormalization()
    {
        using var input = new MemoryStream(Convert.FromBase64String(Jpeg));
        var result = new JpegHeightmapLoader().LoadHeightmap(input);
        for (int z = 0; z < 3; z++)
            for (int x = 0; x < 2; x++)
                Assert.InRange(Math.Abs(result.Heights[x,z]-(z*2+x)/5f),0f,1.1f/255);
    }

    [Fact]
    public void FileAndStreamAgreeAndInvalidInputFails()
    {
        var path=Path.GetTempFileName();
        try {
            File.WriteAllBytes(path,Convert.FromBase64String(Png));
            Assert.Equal(1f,new PngHeightmapLoader().LoadHeightmap(path).Heights[1,2]);
        } finally {File.Delete(path);}
        Assert.Throws<ArgumentNullException>(()=>new PngHeightmapLoader().LoadHeightmap((Stream)null!));
        Assert.ThrowsAny<Exception>(()=>new PngHeightmapLoader().LoadHeightmap(new MemoryStream([1,2,3])));
    }
}
