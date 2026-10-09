using System.Numerics;
using Altruist.Physx.ThreeD;
using BepuPhysics.Collidables;
using FluentAssertions;

namespace Tests.Altruist.Physx.ThreeD;

public class BepuShapeTests
{
    private static readonly PhysxRay3D AlongZ = new(new Vector3(0f, 0f, -10f), new Vector3(0f, 0f, 10f));

    private static float FirstHitT(BepuTestWorld t) => t.Engine.RayCast(AlongZ).Single().T;

    [Fact]
    public void every_attached_collider_is_part_of_the_body_shape()
    {
        using var t = new BepuTestWorld();
        var body = t.Static(Vector3.Zero, new Vector3(0.25f));

        t.Attach(body, PhysxCollider3D.CreateSphere(2f));
        t.Attach(body, PhysxCollider3D.CreateSphere(0.5f));

        FirstHitT(t).Should().BeApproximately(8f, 1e-3f);
    }

    [Fact]
    public void removing_a_collider_keeps_the_other_colliders()
    {
        using var t = new BepuTestWorld();
        var body = t.Static(Vector3.Zero, new Vector3(0.25f));
        t.Attach(body, PhysxCollider3D.CreateSphere(2f));
        var small = t.Attach(body, PhysxCollider3D.CreateSphere(0.5f));

        t.Bodies.RemoveCollider(t.Engine, small);

        FirstHitT(t).Should().BeApproximately(8f, 1e-3f);
    }

    [Fact]
    public void removing_the_last_collider_restores_the_default_box()
    {
        using var t = new BepuTestWorld();
        var body = t.Dynamic(Vector3.Zero, halfExtents: new Vector3(0.25f));
        var sphere = t.Attach(body, PhysxCollider3D.CreateSphere(2f));

        body.RemoveCollider(sphere).Should().BeTrue();

        FirstHitT(t).Should().BeApproximately(9.75f, 1e-3f);
    }

    [Fact]
    public void replaced_shapes_are_released()
    {
        using var t = new BepuTestWorld();
        var body = t.Dynamic(Vector3.Zero);

        for (int i = 0; i < 1000; i++)
        {
            var collider = t.Attach(body, PhysxCollider3D.CreateSphere(0.5f));
            body.RemoveCollider(collider);
        }

        t.Engine.Simulation.Shapes[Sphere.Id].Capacity.Should().BeLessThan(1000);
    }

    [Fact]
    public void a_dynamic_body_rests_on_its_largest_collider()
    {
        using var t = new BepuTestWorld(new Vector3(0f, -10f, 0f));
        var ground = t.Static(new Vector3(0f, -0.5f, 0f), new Vector3(10f, 0.5f, 10f));
        var body = t.Dynamic(new Vector3(0f, 3f, 0f));
        t.Attach(body, PhysxCollider3D.CreateBox(new Vector3(1f)));
        t.Attach(body, PhysxCollider3D.CreateSphere(0.25f));

        t.StepSeconds(3f);

        body.Position.Y.Should().BeApproximately(1f, 0.05f);
    }

    [Fact]
    public void a_heightfield_cannot_share_its_body_with_other_colliders()
    {
        using var t = new BepuTestWorld();
        var body = t.Static(Vector3.Zero, new Vector3(0.5f));
        t.Attach(body, PhysxCollider3D.CreateSphere(1f));

        var act = () => t.Attach(body, PhysxCollider3D.CreateHeightmap(FlatHeightfield(), default));

        act.Should().Throw<NotSupportedException>();
        body.GetColliders().Length.Should().Be(1);
    }

    private static HeightfieldData FlatHeightfield() => new()
    {
        Width = 2,
        Height = 2,
        CellSizeX = 1f,
        CellSizeZ = 1f,
        HeightScale = 1f,
        Heights = new float[2, 2],
    };
}
