using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using FluentAssertions;

namespace Tests.Altruist.Physx.ThreeD;

public class BepuQueryTests
{
    private const uint LayerA = 1u << 8;
    private const uint LayerB = 1u << 9;

    [Fact]
    public void world_ray_cast_honours_the_layer_mask()
    {
        using var t = new BepuTestWorld();
        var world = new PhysxWorld3D(t.Engine);
        t.Static(new Vector3(0f, 0f, 2f), new Vector3(0.5f), new PhysxTag(LayerA));
        var far = t.Static(new Vector3(0f, 0f, 5f), new Vector3(0.5f), new PhysxTag(LayerB));

        var hits = world.RayCast(new PhysxRay3D(Vector3.Zero, new Vector3(0f, 0f, 10f)), maxHits: 4, layerMask: LayerB).ToList();

        hits.Should().ContainSingle().Which.Body.Should().BeSameAs(far);
    }
}
