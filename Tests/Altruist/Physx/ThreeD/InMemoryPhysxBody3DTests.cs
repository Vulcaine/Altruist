using System.Numerics;
using System.Runtime.CompilerServices;
using Altruist.Physx;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.Physx.ThreeD;

public class InMemoryPhysxBody3DTests
{
    private static InMemoryPhysxBody3D Body(PhysxBodyType type, float mass) =>
        new(PhysxBody3D.Create(type, mass, Transform3D.From(Vector3.Zero, Quaternion.Identity, Vector3.One)));

    [Fact]
    public void impulse_changes_a_dynamic_bodys_velocity_by_impulse_over_mass()
    {
        var body = Body(PhysxBodyType.Dynamic, mass: 2f);

        body.ApplyForce(PhysxForce.Impulse3D(new Vector3(4f, 0f, -2f)));

        body.LinearVelocity.Should().Be(new Vector3(2f, 0f, -1f));
    }

    [Fact]
    public void impulse_does_not_move_a_kinematic_body()
    {
        var body = Body(PhysxBodyType.Kinematic, mass: 1f);

        body.ApplyForce(PhysxForce.Impulse3D(new Vector3(4f, 0f, 0f)));

        body.LinearVelocity.Should().Be(Vector3.Zero);
    }

    [Fact]
    public void continuous_force_is_rejected_instead_of_dropped()
    {
        var body = Body(PhysxBodyType.Dynamic, mass: 1f);

        var act = () => body.ApplyForce(PhysxForce.Force3D(Vector3.UnitX));

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void torque_is_rejected_instead_of_dropped()
    {
        var body = Body(PhysxBodyType.Dynamic, mass: 1f);

        var act = () => body.ApplyForce(PhysxForce.Torque3D(Vector3.UnitY));

        act.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void movement_input_delta_time_must_be_set_explicitly()
    {
        typeof(MovementPhysxInput).GetProperty(nameof(MovementPhysxInput.DeltaTime))!
            .IsDefined(typeof(RequiredMemberAttribute), inherit: false)
            .Should().BeTrue();
    }
}
