/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming;
using Altruist.Gaming.TwoD;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using Moq;
using System.Numerics;
using Xunit;

namespace Altruist.Gaming.Tests.TwoD;

public class KinematicCharacterController2DTests
{
    private static Mock<IPhysxBody2D> CreateBodyMock(Vector2? pos = null)
    {
        var mock = new Mock<IPhysxBody2D>();
        mock.SetupProperty(b => b.Position, pos ?? Vector2.Zero);
        mock.SetupProperty(b => b.LinearVelocity, Vector2.Zero);
        mock.SetupProperty(b => b.AngularVelocityZ, 0f);
        mock.SetupProperty(b => b.RotationZ, 0f);
        return mock;
    }

    private static Mock<IGameWorldManager2D> CreateWorldMock()
    {
        var physxMock = new Mock<IPhysxWorld2D>();
        physxMock.Setup(p => p.RayCast(It.IsAny<PhysxRay2D>(), It.IsAny<int>()))
            .Returns(Enumerable.Empty<PhysxRaycastHit2D>());

        var worldMock = new Mock<IGameWorldManager2D>();
        worldMock.Setup(w => w.PhysxWorld).Returns(physxMock.Object);
        return worldMock;
    }

    [Fact]
    public void StepWithNoBody_DoesNotThrow()
    {
        var controller = new KinematicCharacterController2D();
        var worldMock = CreateWorldMock();

        var act = () => controller.Step(0.016f, worldMock.Object);

        act.Should().NotThrow();
    }

    [Fact]
    public void SetBody_SetsBodyPosition_IsAccessible()
    {
        var controller = new KinematicCharacterController2D();
        var bodyMock = CreateBodyMock(new Vector2(10, 20));

        controller.SetBody(bodyMock.Object);

        controller.Position.Should().Be(new Vector2(10, 20));
    }

    [Fact]
    public void MoveIntent_AppliesHorizontalVelocity_OnStep()
    {
        var controller = new KinematicCharacterController2D
        {
            MoveSpeed = 5f,
            Acceleration = 10000f
        };
        var bodyMock = CreateBodyMock();
        var worldMock = CreateWorldMock();

        controller.SetBody(bodyMock.Object);
        controller.MoveIntent(1f); // move right
        controller.Step(0.016f, worldMock.Object);

        // Position should have moved right
        bodyMock.Object.Position.X.Should().BeGreaterThan(0f);
    }

    [Fact]
    public void SimpleJumpAbility2D_DoesNotJump_WhenAirborne()
    {
        var jump = new SimpleJumpAbility2D { JumpSpeed = 10f };

        var bodyMock = CreateBodyMock();
        var ctx = new CharacterMotorContext2D(bodyMock.Object, new Mock<IGameWorldManager2D>().Object)
        {
            IsGrounded = false,
            JumpPressed = true,
            Velocity = Vector2.Zero
        };

        jump.Step(0.016f, ref ctx);

        ctx.Velocity.Y.Should().Be(0f);
    }

    [Fact]
    public void SimpleJumpAbility2D_Jumps_WhenGroundedAndPressed()
    {
        var jump = new SimpleJumpAbility2D { JumpSpeed = 10f };

        var bodyMock = CreateBodyMock();
        var ctx = new CharacterMotorContext2D(bodyMock.Object, new Mock<IGameWorldManager2D>().Object)
        {
            IsGrounded = true,
            JumpPressed = true,
            Velocity = Vector2.Zero
        };

        jump.Step(0.016f, ref ctx);

        ctx.Velocity.Y.Should().Be(10f);
    }

    [Fact]
    public void SprintIntent_IncreasesTargetSpeed()
    {
        var controller = new KinematicCharacterController2D
        {
            MoveSpeed = 5f,
            SprintSpeed = 10f,
            Acceleration = 10000f
        };
        var bodyMock = CreateBodyMock();
        var worldMock = CreateWorldMock();

        controller.SetBody(bodyMock.Object);
        controller.SprintIntent(true);
        controller.MoveIntent(1f);
        controller.Step(0.016f, worldMock.Object);

        // With sprinting, X velocity should be higher (heading toward 10 m/s)
        controller.Velocity.X.Should().BeGreaterThan(0f);
    }
}
