using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.Physx.ThreeD;

public class BepuBodyTests
{
    private const float Dt = 1f / 60f;

    [Fact]
    public void continuous_force_acts_over_every_timestep_of_the_next_step()
    {
        using var t = new BepuTestWorld();
        var body = t.Dynamic(Vector3.Zero, mass: 1f);

        body.ApplyForce(PhysxForce.Force3D(new Vector3(10f, 0f, 0f)));
        t.Engine.Step(4f * Dt + 1e-5f);

        body.LinearVelocity.X.Should().BeApproximately(10f * 4f * Dt, 1e-4f);
    }

    [Fact]
    public void continuous_force_is_cleared_after_the_step_that_applied_it()
    {
        using var t = new BepuTestWorld();
        var body = t.Dynamic(Vector3.Zero, mass: 1f);
        body.ApplyForce(PhysxForce.Force3D(new Vector3(10f, 0f, 0f)));
        t.Engine.Step(Dt);
        var afterFirst = body.LinearVelocity.X;

        t.Engine.Step(Dt);

        body.LinearVelocity.X.Should().BeApproximately(afterFirst, 1e-6f);
    }

    [Fact]
    public void a_step_without_a_timestep_keeps_the_force_for_the_next_one()
    {
        using var t = new BepuTestWorld();
        var body = t.Dynamic(Vector3.Zero, mass: 1f);

        body.ApplyForce(PhysxForce.Force3D(new Vector3(10f, 0f, 0f)));
        t.Engine.Step(Dt * 0.5f);
        t.Engine.Step(Dt * 0.5f + 1e-5f);

        body.LinearVelocity.X.Should().BeApproximately(10f * Dt, 1e-4f);
    }

    [Fact]
    public void torque_is_integrated_over_the_timestep_not_applied_as_an_impulse()
    {
        using var t = new BepuTestWorld();
        var body = t.Dynamic(Vector3.Zero, mass: 1f); // default unit box: inertia 1/6 about every axis

        body.ApplyForce(PhysxForce.Torque3D(new Vector3(0f, 1f, 0f)));
        t.Engine.Step(Dt);

        body.AngularVelocity.Y.Should().BeApproximately(1f * Dt * 6f, 1e-3f);
    }

    [Fact]
    public void moving_a_static_body_moves_it_for_queries()
    {
        using var t = new BepuTestWorld();
        var body = t.Static(Vector3.Zero, new Vector3(0.5f));

        body.Position = new Vector3(0f, 0f, 20f);

        var hits = t.Engine.RayCast(new PhysxRay3D(new Vector3(0f, 0f, 15f), new Vector3(0f, 0f, 25f))).ToList();
        hits.Should().ContainSingle().Which.T.Should().BeApproximately(4.5f, 1e-3f);
        t.Engine.RayCast(new PhysxRay3D(new Vector3(0f, 0f, -5f), new Vector3(0f, 0f, 5f))).Should().BeEmpty();
    }

    [Fact]
    public void setting_mass_changes_how_the_body_responds_to_impulses()
    {
        using var t = new BepuTestWorld();
        var body = t.Dynamic(Vector3.Zero, mass: 1f);

        body.Mass = 4f;
        body.ApplyForce(PhysxForce.Impulse3D(new Vector3(4f, 0f, 0f)));

        body.LinearVelocity.X.Should().BeApproximately(1f, 1e-5f);
        body.Mass.Should().Be(4f);
    }

    [Fact]
    public void making_a_dynamic_body_kinematic_stops_gravity_and_impulses()
    {
        using var t = new BepuTestWorld(new Vector3(0f, -10f, 0f));
        var body = t.Dynamic(Vector3.Zero, mass: 1f);

        body.Type = PhysxBodyType.Kinematic;
        body.ApplyForce(PhysxForce.Impulse3D(new Vector3(4f, 0f, 0f)));
        t.StepSeconds(0.5f);

        body.LinearVelocity.Should().Be(Vector3.Zero);
        body.Position.Should().Be(Vector3.Zero);
        body.Mass.Should().Be(0f);
    }

    [Fact]
    public void making_a_kinematic_body_dynamic_lets_gravity_act()
    {
        using var t = new BepuTestWorld(new Vector3(0f, -10f, 0f));
        var body = t.Kinematic(Vector3.Zero);

        body.Type = PhysxBodyType.Dynamic;
        t.StepSeconds(0.5f);

        body.LinearVelocity.Y.Should().BeLessThan(-4f);
    }

    [Fact]
    public void static_bodies_reject_type_and_mass_changes()
    {
        using var t = new BepuTestWorld();
        var body = t.Static(Vector3.Zero, new Vector3(0.5f));

        ((Action)(() => body.Type = PhysxBodyType.Dynamic)).Should().Throw<InvalidOperationException>();
        ((Action)(() => body.Mass = 1f)).Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void a_body_is_queryable_and_listed_without_add_body()
    {
        using var t = new BepuTestWorld();
        var body = t.Bodies.CreateBody(t.Engine, PhysxBody3D.Create(
            PhysxBodyType.Static, 0f, Transform3D.From(new Vector3(0f, 0f, 5f), Quaternion.Identity, new Vector3(0.5f))));
        var collider = t.Colliders.CreateCollider(PhysxCollider3D.CreateSphere(1f));
        t.Bodies.AddCollider(t.Engine, body, collider);

        t.Engine.Bodies.Should().Contain(body);
        t.Engine.RayCast(new PhysxRay3D(Vector3.Zero, new Vector3(0f, 0f, 10f)))
            .Should().ContainSingle().Which.Body.Should().BeSameAs(body);

        t.Bodies.RemoveCollider(t.Engine, collider);
        body.GetColliders().Length.Should().Be(0);
    }

    [Fact]
    public void add_body_rejects_a_body_of_another_engine()
    {
        using var t = new BepuTestWorld();
        using var other = new BepuTestWorld();
        var foreign = other.Dynamic(Vector3.Zero);

        var act = () => t.Engine.AddBody(foreign);

        act.Should().Throw<InvalidOperationException>();
    }
}
