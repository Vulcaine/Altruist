
namespace Altruist.Gaming.ThreeD;

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
/// Provider-agnostic loader: only knows how to read a heightmap from a stream.
/// No physics / no BEPU concepts here.
/// </summary>
public interface IHeightmapLoader
{
    HeightmapData LoadHeightmapData(Stream stream);
}

[Service(typeof(IHeightmapLoader))]
public sealed class HeightmapLoader : IHeightmapLoader
{
    public HeightmapData LoadHeightmapData(Stream stream)
    {
        using var br = new BinaryReader(stream, System.Text.Encoding.UTF8, leaveOpen: true);

        int width = br.ReadInt32();
        int height = br.ReadInt32();
        float cellX = br.ReadSingle();
        float cellZ = br.ReadSingle();
        float hScale = br.ReadSingle();

        var heights = new float[width, height];

        for (int z = 0; z < height; z++)
            for (int x = 0; x < width; x++)
            {
                heights[x, z] = br.ReadSingle();
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
