using System.Numerics;
using Altruist.Gaming.ThreeD;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;
using Moq;

namespace Tests.Gaming.Physx.ThreeD;

public sealed class KinematicCharacterController3DTests
{
    [Fact]
    public void SetBody_ShouldKeepPublicPositionAtPivot_WhenBodyUsesCapsuleCenter()
    {
        var body = CreateBody(new Vector3(10f, 20f, 30f));
        var controller = CreateController();

        controller.SetBody(body);

        AssertVector(new Vector3(10f, 20.9f, 30f), body.Position);
        AssertVector(new Vector3(10f, 20f, 30f), controller.Position);
    }

    [Fact]
    public void Step_ShouldMoveFromPivotWithoutChangingPivotHeight()
    {
        var body = CreateBody(new Vector3(0f, 100f, 0f));
        var controller = CreateController();
        controller.SetBody(body);
        controller.MovementStats = new CharacterMovementStats3D(
            WalkSpeed: 2f,
            SprintSpeed: 6f,
            MoveSpeedMultiplier: 1f,
            RotationSpeedDegPerSec: 0f);
        controller.SetMovementInput(new CharacterRealtimeWasdInput3D(
            MoveX: 0f,
            MoveZ: 1f,
            LookDirection: Vector3.Zero,
            CameraYaw: MathF.PI * 0.5f,
            ExternalFacingYaw: 0f,
            UseExternalFacing: false,
            Sprint: false,
            Jump: false));

        controller.Step(1f, Mock.Of<IGameWorldManager3D>());

        Assert.InRange(controller.Position.X, 1.99f, 2.01f);
        Assert.InRange(controller.Position.Y, 99.99f, 100.01f);
        Assert.InRange(controller.Position.Z, -0.01f, 0.01f);
        Assert.InRange(controller.Yaw, (MathF.PI * 0.5f) - 0.001f, (MathF.PI * 0.5f) + 0.001f);
        Assert.InRange(body.Position.Y, 100.89f, 100.91f);
    }

    [Fact]
    public void Step_ShouldApplyExternalFacingYawWithoutRotatingAroundPivot()
    {
        var body = CreateBody(new Vector3(3f, 50f, 7f));
        var controller = CreateController();
        controller.SetBody(body);
        controller.SetMovementInput(new CharacterRealtimeWasdInput3D(
            MoveX: 0f,
            MoveZ: 0f,
            LookDirection: Vector3.UnitZ,
            CameraYaw: 0f,
            ExternalFacingYaw: 1.25f,
            UseExternalFacing: true,
            Sprint: false,
            Jump: false));

        controller.Step(1f, Mock.Of<IGameWorldManager3D>());

        Assert.InRange(controller.Yaw, 1.249f, 1.251f);
        AssertVector(new Vector3(3f, 50f, 7f), controller.Position);
        AssertVector(new Vector3(3f, 50.9f, 7f), body.Position);
    }

    [Fact]
    public void Step_ShouldClearClickToMoveInput_WhenTargetAlreadyReached()
    {
        var body = CreateBody(new Vector3(5f, 12f, 8f));
        var controller = CreateController();
        controller.SetBody(body);
        controller.SetMovementInput(new CharacterClickToMoveInput3D(
            TargetWorldPosition: new Vector3(5f, 12f, 8f),
            ArrivalDistance: 0.3f,
            Sprint: true));

        Assert.True(controller.HasActiveMovementInput);

        controller.Step(1f, Mock.Of<IGameWorldManager3D>());

        Assert.True(controller.LastMovementFrame.ArrivedAtTarget);
        Assert.False(controller.HasActiveMovementInput);
        AssertVector(new Vector3(5f, 12f, 8f), controller.Position);
    }

    private static KinematicCharacterController3D CreateController() => new()
    {
        MovementProfile = MmoMovementProfile3D.Default,
        PivotToCenterY = 0.9f,
        UseCapsuleSweeps = false,
        Acceleration = 1000f,
        Deceleration = 1000f,
    };

    private static InMemoryPhysxBody3D CreateBody(Vector3 pivot)
    {
        var transform = Transform3D.From(pivot, Quaternion.Identity, Vector3.One);
        var desc = PhysxBody3D.Create(PhysxBodyType.Kinematic, mass: 0f, transform, isKinematic: true);
        return new InMemoryPhysxBody3D(desc);
    }

    private static void AssertVector(Vector3 expected, Vector3 actual, float tolerance = 0.0001f)
    {
        Assert.InRange(actual.X, expected.X - tolerance, expected.X + tolerance);
        Assert.InRange(actual.Y, expected.Y - tolerance, expected.Y + tolerance);
        Assert.InRange(actual.Z, expected.Z - tolerance, expected.Z + tolerance);
    }
}
