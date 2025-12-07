using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Altruist.Gaming.ThreeD
{
    public sealed class HeightmapData
    {
        public int Width { get; init; }
        public int Height { get; init; }

        /// <summary>World-space distance between samples along X.</summary>
        public float CellSizeX { get; init; }

        /// <summary>World-space distance between samples along Z.</summary>
        public float CellSizeZ { get; init; }

        /// <summary>Multiplier from stored height to world-space Y.</summary>
        public float HeightScale { get; init; }

        /// <summary>Heights[x, z] in whatever units you decided (usually 0..1 before HeightScale).</summary>
        public float[,] Heights { get; init; } = default!;
    }

    /// <summary>
    /// Common contract for all concrete heightmap loaders (RAW, PNG, TIFF, JPEG, ...).
    /// </summary>
    public interface IHeightmapFormatLoader
    {
        HeightmapData LoadHeightmap(Stream stream);
    }

    /// <summary>
    /// RAW / R16 / R32 loader.
    /// </summary>
    public interface IRawHeightmapLoader : IHeightmapFormatLoader { }

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
    ///   _heightmapLoader.RAW.LoadHeightmap(stream);
    /// </summary>
    public interface IHeightmapLoader
    {
        IRawHeightmapLoader RAW { get; }
        IPngHeightmapLoader PNG { get; }
        ITiffHeightmapLoader TIFF { get; }
        IJpegHeightmapLoader JPEG { get; }
    }

    [Service(typeof(IHeightmapLoader))]
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

        public HeightmapData LoadHeightmap(Stream stream)
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

            return new HeightmapData
            {
                Width = width,
                Height = height,
                CellSizeX = DefaultCellSizeX,
                CellSizeZ = DefaultCellSizeZ,
                HeightScale = DefaultHeightScale,
                Heights = heights
            };
        }
    }

    /// <summary>
    /// RAW loader implementing your existing binary format:
    /// [int32 width][int32 height][float cellX][float cellZ][float heightScale][width*height float samples].
    /// This is essentially your old HeightmapLoader, just renamed and wired into the new abstraction.
    /// </summary>
    [Service(typeof(IRawHeightmapLoader))]
    public sealed class RawHeightmapLoader : IRawHeightmapLoader
    {
        public HeightmapData LoadHeightmap(Stream stream)
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

            return new HeightmapData
            {
                Width = width,
                Height = height,
                CellSizeX = cellX,
                CellSizeZ = cellZ,
                HeightScale = hScale,
                Heights = heights
            };
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
}
