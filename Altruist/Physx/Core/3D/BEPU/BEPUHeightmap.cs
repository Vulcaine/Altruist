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
    /// <param name="data">Height samples.</param>
    /// <param name="pool">BEPU buffer pool that owns the triangle buffer (normally the simulation's pool).</param>
    /// <returns>A mesh in the heightfield's local space (origin at sample (0, 0)).</returns>
    Mesh LoadHeightmapMesh(HeightfieldData data, BufferPool pool);
}

/// <summary>
/// BEPU heightmap loader: forwards the format loaders of the core <see cref="IHeightmapLoader"/> and builds triangle
/// meshes for heightfield colliders. Registered in DI as a singleton <see cref="IHeightmapLoader3D"/> when the config section
/// <c>altruist:game</c> is present.
/// </summary>
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
    /// <inheritdoc/>
    public IRawHeightmapLoader RAW => _coreLoader.RAW;
    /// <inheritdoc/>
    public IPngHeightmapLoader PNG => _coreLoader.PNG;
    /// <inheritdoc/>
    public ITiffHeightmapLoader TIFF => _coreLoader.TIFF;
    /// <inheritdoc/>
    public IJpegHeightmapLoader JPEG => _coreLoader.JPEG;

    /// <summary>
    /// Builds a BEPU mesh from the given <see cref="HeightfieldData"/>: two triangles per grid cell, vertex
    /// <c>(x, z)</c> at <c>(x * CellSizeX, Heights[x, z] * HeightScale, z * CellSizeZ)</c>, the same surface that
    /// <see cref="HeightfieldDataExtensions.SampleHeight"/> samples.
    /// </summary>
    /// <remarks>Allocates the triangle buffer from <paramref name="pool"/>.</remarks>
    /// <param name="hf">Height samples (at least 2×2).</param>
    /// <param name="pool">Buffer pool that will own the mesh's triangles.</param>
    public Mesh LoadHeightmapMesh(HeightfieldData hf, BufferPool pool) => BepuHeightfieldMesh.Create(hf, pool);
}

/// <summary>Pure heightfield-to-mesh conversion shared by <see cref="BepuHeightmapLoader"/> and <see cref="BepuWorldEngine3D"/>.</summary>
internal static class BepuHeightfieldMesh
{
    /// <summary>See <see cref="BepuHeightmapLoader.LoadHeightmapMesh"/>.</summary>
    /// <param name="hf">Height samples (at least 2×2).</param>
    /// <param name="pool">Buffer pool that will own the mesh's triangles.</param>
    public static Mesh Create(HeightfieldData hf, BufferPool pool)
    {
        int width = hf.Width;
        int length = hf.Height;

        float cellSizeX = hf.CellSizeX;
        float cellSizeZ = hf.CellSizeZ;

        int quadCount = (width - 1) * (length - 1);
        int triangleCount = quadCount * 2;

        pool.Take(triangleCount, out Buffer<Triangle> triangles);

        int triIndex = 0;

        for (int z = 0; z < length - 1; z++)
        {
            for (int x = 0; x < width - 1; x++)
            {
                float h00 = hf.Heights[x, z] * hf.HeightScale;
                float h10 = hf.Heights[x + 1, z] * hf.HeightScale;
                float h01 = hf.Heights[x, z + 1] * hf.HeightScale;
                float h11 = hf.Heights[x + 1, z + 1] * hf.HeightScale;

                var v00 = new Vector3(x * cellSizeX, h00, z * cellSizeZ);
                var v10 = new Vector3((x + 1) * cellSizeX, h10, z * cellSizeZ);
                var v01 = new Vector3(x * cellSizeX, h01, (z + 1) * cellSizeZ);
                var v11 = new Vector3((x + 1) * cellSizeX, h11, (z + 1) * cellSizeZ);

                ref var t0 = ref triangles[triIndex++];
                t0.A = v00;
                t0.B = v10;
                t0.C = v01;

                ref var t1 = ref triangles[triIndex++];
                t1.A = v10;
                t1.B = v11;
                t1.C = v01;
            }
        }

        return new Mesh(triangles, new Vector3(1f, 1f, 1f), pool);
    }
}
