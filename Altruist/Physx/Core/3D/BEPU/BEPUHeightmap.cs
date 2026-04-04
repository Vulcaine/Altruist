using System.Numerics;

using BepuPhysics.Collidables;

using BepuUtilities.Memory;

namespace Altruist.Physx.ThreeD;

/// <summary>
/// BEPU-specific extension: can both use all the 2D heightmap loaders (RAW/PNG/TIFF/JPEG)
/// *and* build BEPU meshes from a <see cref="HeightfieldData"/>.
/// </summary>
public interface IHeightmapLoader3D : IHeightmapLoader
{
    /// <summary>
    /// Builds a BEPU <see cref="Mesh"/> from already loaded heightmap data.
    /// </summary>
    Mesh LoadHeightmapMesh(HeightfieldData data, BufferPool pool);
}

[Service(typeof(IHeightmapLoader3D))]
[ConditionalOnConfig("altruist:game")]
public sealed class BepuHeightmapLoader : IHeightmapLoader3D
{
    private readonly IHeightmapLoader _coreLoader;

    /// <summary>
    /// Wrap the core 2D heightmap loader facade so we can reuse all format loaders.
    /// </summary>
    public BepuHeightmapLoader(IHeightmapLoader coreLoader)
    {
        _coreLoader = coreLoader ?? throw new ArgumentNullException(nameof(coreLoader));
    }

    // IHeightmapLoader facade passthrough
    public IRawHeightmapLoader RAW => _coreLoader.RAW;
    public IPngHeightmapLoader PNG => _coreLoader.PNG;
    public ITiffHeightmapLoader TIFF => _coreLoader.TIFF;
    public IJpegHeightmapLoader JPEG => _coreLoader.JPEG;

    /// <summary>
    /// Builds a BEPU mesh from the given <see cref="HeightfieldData"/>.
    /// This matches the layout used by the RAW/PNG/TIFF/JPEG loaders:
    /// Heights[x, z] with cell sizes in X/Z and height scaled by <see cref="HeightfieldData.HeightScale"/>.
    /// </summary>
    public Mesh LoadHeightmapMesh(HeightfieldData hf, BufferPool pool)
    {
        int width = hf.Width;
        int length = hf.Height;

        float cellSizeX = hf.CellSizeX;
        float cellSizeZ = hf.CellSizeZ;

        int quadCount = (width - 1) * (length - 1);

        // 2 triangles per quad, and we add both windings => 4 triangles per quad
        int triangleCount = quadCount * 2;

        pool.Take(triangleCount, out Buffer<Triangle> triangles);

        int triIndex = 0;

        for (int z = 0; z < length - 1; z++)
        {
            for (int x = 0; x < width - 1; x++)
            {
                float h00 = hf.Heights[x, z];
                float h10 = hf.Heights[x + 1, z];
                float h01 = hf.Heights[x, z + 1];
                float h11 = hf.Heights[x + 1, z + 1];

                var v00 = new Vector3(x * cellSizeX, h00, z * cellSizeZ);
                var v10 = new Vector3((x + 1) * cellSizeX, h10, z * cellSizeZ);
                var v01 = new Vector3(x * cellSizeX, h01, (z + 1) * cellSizeZ);
                var v11 = new Vector3((x + 1) * cellSizeX, h11, (z + 1) * cellSizeZ);

                // Triangle 0 (one winding)
                // ref var t0 = ref triangles[triIndex++];
                // t0.A = v00;
                // t0.B = v01;
                // t0.C = v10;

                // Triangle 0 (reverse winding)
                ref var t0r = ref triangles[triIndex++];
                t0r.A = v00;
                t0r.B = v10;
                t0r.C = v01;

                // Triangle 1 (one winding)
                // ref var t1 = ref triangles[triIndex++];
                // t1.A = v10;
                // t1.B = v01;
                // t1.C = v11;

                // Triangle 1 (reverse winding)
                ref var t1r = ref triangles[triIndex++];
                t1r.A = v10;
                t1r.B = v11;
                t1r.C = v01;
            }
        }

        return new Mesh(triangles, new Vector3(1f, 1f, 1f), pool);
    }
}
