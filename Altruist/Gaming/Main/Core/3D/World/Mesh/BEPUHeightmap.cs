using System.Numerics;

using BepuPhysics.Collidables;

using BepuUtilities.Memory;

namespace Altruist.Gaming.ThreeD;

/// <summary>
/// BEPU-specific extension: can both load heightmaps and build BEPU meshes from them.
/// </summary>
public interface IHeightmapLoader3D : IHeightmapLoader
{
    Mesh LoadHeightmapMesh(HeightmapData data, BufferPool pool);
}

[Service(typeof(IHeightmapLoader3D))]
public sealed class BepuHeightmapLoader : IHeightmapLoader3D
{
    private readonly IHeightmapLoader _coreLoader;

    // Optional: wrap the core loader so you can reuse the same binary format implementation.
    public BepuHeightmapLoader(IHeightmapLoader coreLoader)
    {
        _coreLoader = coreLoader;
    }

    public HeightmapData LoadHeightmapData(Stream stream)
        => _coreLoader.LoadHeightmapData(stream);

    public Mesh LoadHeightmapMesh(HeightmapData data, BufferPool pool)
    {
        int width = data.Width;
        int height = data.Height;

        int quadWidth = width - 1;
        int quadHeight = height - 1;
        int triangleCount = quadWidth * quadHeight * 2;

        // Allocate only triangle buffer; we’ll compute vertex positions inline.
        pool.Take<Triangle>(triangleCount, out var triangles);

        for (int z = 0; z < quadHeight; z++)
        {
            for (int x = 0; x < quadWidth; x++)
            {
                int triIndex = (z * quadWidth + x) * 2;

                // Precompute world positions for the quad corners
                float x0 = x * data.CellSizeX;
                float x1 = (x + 1) * data.CellSizeX;
                float z0 = z * data.CellSizeZ;
                float z1 = (z + 1) * data.CellSizeZ;

                float y00 = data.Heights[x, z] * data.HeightScale;
                float y10 = data.Heights[x, z + 1] * data.HeightScale;
                float y01 = data.Heights[x + 1, z] * data.HeightScale;
                float y11 = data.Heights[x + 1, z + 1] * data.HeightScale;

                var v00 = new Vector3(x0, y00, z0);
                var v10 = new Vector3(x0, y10, z1);
                var v01 = new Vector3(x1, y01, z0);
                var v11 = new Vector3(x1, y11, z1);

                // Two triangles per quad; choose consistent winding (here: v00 -> v10 -> v11, etc.)
                ref var t0 = ref triangles[triIndex];
                t0.A = v00;
                t0.B = v10;
                t0.C = v11;

                ref var t1 = ref triangles[triIndex + 1];
                t1.A = v00;
                t1.B = v11;
                t1.C = v01;
            }
        }

        // Mesh takes ownership of the triangles buffer.
        var mesh = new Mesh(triangles, Vector3.One, pool);
        return mesh;
    }
}
