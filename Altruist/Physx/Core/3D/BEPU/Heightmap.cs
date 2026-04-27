using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Altruist.Physx.ThreeD;


/// <summary>
/// Common contract for all concrete heightmap loaders (RAW, PNG, TIFF, JPEG, ...).
/// </summary>
public interface IHeightmapFormatLoader
{
    HeightfieldData LoadHeightmap(Stream stream);
    HeightfieldData LoadHeightmap(string filePath);
}

/// <summary>
/// RAW / R16 / R32 loader.
/// </summary>
public interface IRawHeightmapLoader : IHeightmapFormatLoader
{
    HeightfieldData LoadUInt16Heightmap(Stream stream, RawHeightmapLoadOptions options);
    HeightfieldData LoadUInt16Heightmap(string filePath, RawHeightmapLoadOptions options);
}

public sealed class RawHeightmapLoadOptions
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public float CellSizeX { get; init; } = 1f;
    public float CellSizeZ { get; init; } = 1f;
    public float HeightScale { get; init; } = 1f;
    public bool FlipZ { get; init; }
    public bool LittleEndian { get; init; } = true;
}

/// <summary>
/// 16-bit PNG loader.
/// </summary>
public interface IPngHeightmapLoader : IHeightmapFormatLoader { }

/// <summary>
/// TIFF / EXR loader.
/// </summary>
public interface ITiffHeightmapLoader : IHeightmapFormatLoader { }

/// <summary>
/// JPEG (if you really want lossy heightmaps).
/// </summary>
public interface IJpegHeightmapLoader : IHeightmapFormatLoader { }

/// <summary>
/// Facade that groups all format-specific loaders behind a single service.
/// Usage:
///   _heightmapLoader.PNG.LoadHeightmap(stream);
///   _heightmapLoader.RAW.LoadHeightmap("terrain.hmap");
/// </summary>
public interface IHeightmapLoader
{
    IRawHeightmapLoader RAW { get; }
    IPngHeightmapLoader PNG { get; }
    ITiffHeightmapLoader TIFF { get; }
    IJpegHeightmapLoader JPEG { get; }
}

[Service(typeof(IHeightmapLoader))]
[ConditionalOnConfig("altruist:game")]
public sealed class HeightmapLoader : IHeightmapLoader
{
    public HeightmapLoader(
        IRawHeightmapLoader raw,
        IPngHeightmapLoader png,
        ITiffHeightmapLoader tiff,
        IJpegHeightmapLoader jpeg)
    {
        RAW = raw ?? throw new ArgumentNullException(nameof(raw));
        PNG = png ?? throw new ArgumentNullException(nameof(png));
        TIFF = tiff ?? throw new ArgumentNullException(nameof(tiff));
        JPEG = jpeg ?? throw new ArgumentNullException(nameof(jpeg));
    }

    public IRawHeightmapLoader RAW { get; }
    public IPngHeightmapLoader PNG { get; }
    public ITiffHeightmapLoader TIFF { get; }
    public IJpegHeightmapLoader JPEG { get; }
}

/// <summary>
/// Common base for ImageSharp-based heightmap loaders.
/// Derived types only need to define how to load the Image and how to convert a pixel to a height.
/// </summary>
public abstract class AbstractHeightmapLoader<TPixel> : IHeightmapFormatLoader
    where TPixel : unmanaged, IPixel<TPixel>
{
    /// <summary>
    /// Default cell size along X. Override if your format encodes this elsewhere.
    /// </summary>
    protected virtual float DefaultCellSizeX => 1.0f;

    /// <summary>
    /// Default cell size along Z. Override if your format encodes this elsewhere.
    /// </summary>
    protected virtual float DefaultCellSizeZ => 1.0f;

    /// <summary>
    /// Default height scale. Override if your format encodes this elsewhere.
    /// </summary>
    protected virtual float DefaultHeightScale => 1.0f;

    /// <summary>
    /// Implementations load and return an ImageSharp image of the correct pixel type.
    /// </summary>
    protected abstract Image<TPixel> LoadImage(Stream stream);

    /// <summary>
    /// Implementations convert a pixel into a normalized height value.
    /// </summary>
    protected abstract float ConvertPixelToHeight(TPixel pixel);

    public HeightfieldData LoadHeightmap(Stream stream)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        using var image = LoadImage(stream);

        int width = image.Width;
        int height = image.Height;

        var heights = new float[width, height];

        // ImageSharp v1/v2 API: ProcessPixelRows + accessor.GetRowSpan(y)
        image.ProcessPixelRows(accessor =>
        {
            for (int z = 0; z < height; z++)
            {
                var rowSpan = accessor.GetRowSpan(z);

                for (int x = 0; x < width; x++)
                {
                    heights[x, z] = ConvertPixelToHeight(rowSpan[x]);
                }
            }
        });

        return new HeightfieldData
        {
            Width = width,
            Height = height,
            CellSizeX = DefaultCellSizeX,
            CellSizeZ = DefaultCellSizeZ,
            HeightScale = DefaultHeightScale,
            Heights = heights
        };
    }

    public HeightfieldData LoadHeightmap(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must be non-empty.", nameof(filePath));

        using var fs = File.OpenRead(filePath);
        return LoadHeightmap(fs);
    }

}

public static class HeightfieldDataExtensions
{
    public static float SampleHeight(this HeightfieldData heightfield, float localX, float localZ)
    {
        if (heightfield.Width <= 0 || heightfield.Height <= 0)
            return 0f;

        float maxX = MathF.Max(0f, (heightfield.Width - 1) * heightfield.CellSizeX);
        float maxZ = MathF.Max(0f, (heightfield.Height - 1) * heightfield.CellSizeZ);
        if (maxX <= 0f || maxZ <= 0f)
            return heightfield.Heights[0, 0] * heightfield.HeightScale;

        localX = Math.Clamp(localX, 0f, maxX);
        localZ = Math.Clamp(localZ, 0f, maxZ);

        float sx = localX / heightfield.CellSizeX;
        float sz = localZ / heightfield.CellSizeZ;

        int x0 = Math.Clamp((int)MathF.Floor(sx), 0, heightfield.Width - 1);
        int z0 = Math.Clamp((int)MathF.Floor(sz), 0, heightfield.Height - 1);
        int x1 = Math.Min(x0 + 1, heightfield.Width - 1);
        int z1 = Math.Min(z0 + 1, heightfield.Height - 1);
        float fx = sx - x0;
        float fz = sz - z0;

        float h00 = heightfield.Heights[x0, z0] * heightfield.HeightScale;
        float h10 = heightfield.Heights[x1, z0] * heightfield.HeightScale;
        float h01 = heightfield.Heights[x0, z1] * heightfield.HeightScale;
        float h11 = heightfield.Heights[x1, z1] * heightfield.HeightScale;

        float hx0 = Lerp(h00, h10, fx);
        float hx1 = Lerp(h01, h11, fx);
        return Lerp(hx0, hx1, fz);
    }

    public static System.Numerics.Vector3 SampleNormal(this HeightfieldData heightfield, float localX, float localZ, float step = 0.5f)
    {
        float hL = heightfield.SampleHeight(localX - step, localZ);
        float hR = heightfield.SampleHeight(localX + step, localZ);
        float hD = heightfield.SampleHeight(localX, localZ - step);
        float hU = heightfield.SampleHeight(localX, localZ + step);
        var normal = new System.Numerics.Vector3(hL - hR, step * 2f, hD - hU);
        return normal.LengthSquared() > 1e-8f ? System.Numerics.Vector3.Normalize(normal) : System.Numerics.Vector3.UnitY;
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>
/// RAW loader implementing your existing binary format:
/// [int32 width][int32 height][float cellX][float cellZ][float heightScale][width*height float samples].
/// This is essentially your old HeightmapLoader, just renamed and wired into the new abstraction.
/// </summary>
[Service(typeof(IRawHeightmapLoader))]
public sealed class RawHeightmapLoader : IRawHeightmapLoader
{
    public HeightfieldData LoadHeightmap(Stream stream)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));

        using var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        int width = br.ReadInt32();
        int height = br.ReadInt32();
        float cellX = br.ReadSingle();
        float cellZ = br.ReadSingle();
        float hScale = br.ReadSingle();

        var heights = new float[width, height];

        for (int z = 0; z < height; z++)
        {
            for (int x = 0; x < width; x++)
            {
                heights[x, z] = br.ReadSingle();
            }
        }

        return new HeightfieldData
        {
            Width = width,
            Height = height,
            CellSizeX = cellX,
            CellSizeZ = cellZ,
            HeightScale = hScale,
            Heights = heights
        };
    }

    public HeightfieldData LoadHeightmap(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must be non-empty.", nameof(filePath));

        using var fs = File.OpenRead(filePath);
        return LoadHeightmap(fs);
    }

    public HeightfieldData LoadUInt16Heightmap(Stream stream, RawHeightmapLoadOptions options)
    {
        if (stream == null)
            throw new ArgumentNullException(nameof(stream));
        if (options.Width <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Heightmap width must be positive.");
        if (options.Height <= 0)
            throw new ArgumentOutOfRangeException(nameof(options), "Heightmap height must be positive.");

        long expectedBytes = (long)options.Width * options.Height * sizeof(ushort);
        if (stream.CanSeek && stream.Length - stream.Position < expectedBytes)
            throw new InvalidDataException($"Raw uint16 heightmap is too small. Expected {expectedBytes} bytes, found {stream.Length - stream.Position}.");

        var heights = new float[options.Width, options.Height];
        Span<byte> pair = stackalloc byte[2];

        for (int z = 0; z < options.Height; z++)
        {
            int targetZ = options.FlipZ ? options.Height - 1 - z : z;
            for (int x = 0; x < options.Width; x++)
            {
                if (stream.Read(pair) != pair.Length)
                    throw new EndOfStreamException("Unexpected end of raw uint16 heightmap stream.");

                ushort rawHeight = options.LittleEndian
                    ? (ushort)(pair[0] | (pair[1] << 8))
                    : (ushort)((pair[0] << 8) | pair[1]);

                heights[x, targetZ] = rawHeight * options.HeightScale;
            }
        }

        return new HeightfieldData
        {
            Width = options.Width,
            Height = options.Height,
            CellSizeX = options.CellSizeX,
            CellSizeZ = options.CellSizeZ,
            HeightScale = 1f,
            Heights = heights
        };
    }

    public HeightfieldData LoadUInt16Heightmap(string filePath, RawHeightmapLoadOptions options)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new ArgumentException("File path must be non-empty.", nameof(filePath));

        using var fs = File.OpenRead(filePath);
        return LoadUInt16Heightmap(fs, options);
    }
}

/// <summary>
/// 16-bit PNG loader.
/// Assumes a single-channel 16-bit grayscale heightmap (L16) with values in [0, 65535],
/// which are normalized to [0, 1] and stored in Heights.
/// Cell sizes and HeightScale are set to 1 by default; adjust externally if needed.
/// </summary>
[Service(typeof(IPngHeightmapLoader))]
public sealed class PngHeightmapLoader : AbstractHeightmapLoader<L16>, IPngHeightmapLoader
{
    protected override Image<L16> LoadImage(Stream stream)
        => Image.Load<L16>(stream);

    protected override float ConvertPixelToHeight(L16 pixel)
        => pixel.PackedValue / 65535f;
}

/// <summary>
/// TIFF / EXR loader.
/// Currently implemented for TIFF via ImageSharp and assumes single-channel 16-bit grayscale (L16),
/// normalized to [0, 1]. If you need EXR, you can add a different implementation and wire it similarly.
/// </summary>
[Service(typeof(ITiffHeightmapLoader))]
public sealed class TiffHeightmapLoader : AbstractHeightmapLoader<L16>, ITiffHeightmapLoader
{
    protected override Image<L16> LoadImage(Stream stream)
        => Image.Load<L16>(stream);

    protected override float ConvertPixelToHeight(L16 pixel)
        => pixel.PackedValue / 65535f;
}

/// <summary>
/// JPEG loader (lossy).
/// Assumes an 8-bit grayscale heightmap (L8) or that the luminance channel encodes height.
/// Byte values 0..255 are normalized to [0, 1].
/// </summary>
[Service(typeof(IJpegHeightmapLoader))]
public sealed class JpegHeightmapLoader : AbstractHeightmapLoader<L8>, IJpegHeightmapLoader
{
    protected override Image<L8> LoadImage(Stream stream)
        => Image.Load<L8>(stream);

    protected override float ConvertPixelToHeight(L8 pixel)
        => pixel.PackedValue / 255f;
}
