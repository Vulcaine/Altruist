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

    [Fact]
    public void capsule_cast_can_ignore_the_casters_own_body()
    {
        using var t = new BepuTestWorld();
        var caster = t.Kinematic(Vector3.Zero);
        t.Attach(caster, PhysxCollider3D.CreateCapsule(0.4f, 0.5f));
        var wall = t.Static(new Vector3(0f, 0f, 5f), new Vector3(2f, 2f, 0.5f));

        var hits = t.Engine.CapsuleCast(new PhysxCapsuleCast3D(Vector3.Zero, 0.4f, 0.5f, Vector3.UnitZ, 10f)
        {
            MaxHits = 4,
            IgnoredBody = caster,
        }).ToList();

        hits.Should().ContainSingle().Which.Body.Should().BeSameAs(wall);
        hits[0].T.Should().BeApproximately(4.1f, 1e-2f);
    }

    [Fact]
    public void capsule_cast_uses_the_given_orientation()
    {
        using var t = new BepuTestWorld();
        var post = t.Static(new Vector3(1.5f, 0f, 0f), new Vector3(0.25f));
        var lying = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);

        var upright = t.Engine.CapsuleCast(new PhysxCapsuleCast3D(new Vector3(0f, 3f, 0f), 0.2f, 2f, -Vector3.UnitY, 10f));
        var tilted = t.Engine.CapsuleCast(new PhysxCapsuleCast3D(new Vector3(0f, 3f, 0f), 0.2f, 2f, -Vector3.UnitY, 10f)
        {
            Orientation = lying,
        });

        upright.Should().BeEmpty();
        tilted.Should().ContainSingle().Which.Body.Should().BeSameAs(post);
    }

    [Fact]
    public void capsule_cast_reports_hit_point_and_normal_in_the_right_fields()
    {
        using var t = new BepuTestWorld();
        t.Static(new Vector3(0f, -0.5f, 0f), new Vector3(10f, 0.5f, 10f));

        var hit = t.Engine.CapsuleCast(new Vector3(0f, 5f, 0f), 0.5f, 0.5f, -Vector3.UnitY, 10f).Single();

        hit.Point.Y.Should().BeApproximately(0f, 1e-2f);
        MathF.Abs(hit.Normal.Y).Should().BeApproximately(1f, 1e-3f);
        hit.T.Should().BeApproximately(4f, 1e-2f);
    }
}
