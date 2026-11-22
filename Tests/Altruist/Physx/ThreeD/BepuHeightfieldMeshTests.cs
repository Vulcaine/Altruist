using Altruist.Physx.ThreeD;
using BepuUtilities.Memory;
using FluentAssertions;
using Moq;

namespace Tests.Altruist.Physx.ThreeD;

public class BepuHeightfieldMeshTests
{
    private static HeightfieldData Flat(float height, float heightScale) => new()
    {
        Width = 3,
        Height = 3,
        CellSizeX = 1f,
        CellSizeZ = 1f,
        HeightScale = heightScale,
        Heights = new float[3, 3]
        {
            { height, height, height },
            { height, height, height },
            { height, height, height },
        },
    };

    [Fact]
    public void mesh_vertices_apply_height_scale_like_sample_height()
    {
        var data = Flat(height: 1f, heightScale: 2.5f);
        var loader = new BepuHeightmapLoader(Mock.Of<IHeightmapLoader>());
        var pool = new BufferPool();

        var mesh = loader.LoadHeightmapMesh(data, pool);

        mesh.Triangles.Length.Should().Be(8);
        for (int i = 0; i < mesh.Triangles.Length; i++)
        {
            mesh.Triangles[i].A.Y.Should().Be(data.SampleHeight(0.5f, 0.5f));
            mesh.Triangles[i].B.Y.Should().Be(2.5f);
            mesh.Triangles[i].C.Y.Should().Be(2.5f);
        }

        mesh.Dispose(pool);
        pool.Clear();
    }
}
