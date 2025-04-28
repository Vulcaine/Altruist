namespace Altruist.Physx.Movement;

using FarseerPhysics.Dynamics;
using Microsoft.Xna.Framework;
using Xunit;
using FarseerPhysics.Factories;

public class ForwardMovementPhysxTests
{
    private World world;
    private Body body;
    private ForwardMovementPhysx forwardMovementPhysx;

    public ForwardMovementPhysxTests()
    {
        // Initialize the physics world
        world = new World(Vector2.Zero);
        forwardMovementPhysx = new ForwardMovementPhysx();

        body = BodyFactory.CreateRectangle(world, 1f, 1f, 1f, position: Vector2.Zero);
    }

    [Fact]
    public void ApplyMovement_ShouldMoveForward_WhenMoveForwardIsTrue()
    {
        // Arrange
        var input = new ForwardMovementPhysxInput
        {
            MoveForward = true,
            CurrentSpeed = 5f,
            Acceleration = 2f,
            MaxSpeed = 10f,
            DeltaTime = 1f,
            Turbo = false,
            RotationSpeed = 1f
        };

        body.Rotation = 0f; // Facing right (East)
        body.Position = Vector2.Zero;

        // Act
        forwardMovementPhysx.ApplyMovement(body, input);
        world.Step(1f); // Simulate one step in the world (DeltaTime)

        // Assert
        Assert.Equal(new Vector2(7f, 0f), body.Position);  // Position = 5 + (2 * 1) (based on speed and acceleration)
        Assert.Equal(new Vector2(7f, 0f), body.LinearVelocity);  // LinearVelocity should match velocity direction
        Assert.Equal(7f, input.CurrentSpeed);  // Speed after acceleration
    }

    [Fact]
    public void ApplyMovement_ShouldNotMove_WhenMoveForwardIsFalse()
    {
        // Arrange
        var input = new ForwardMovementPhysxInput
        {
            MoveForward = false,
            CurrentSpeed = 5f,
            Acceleration = 2f,
            MaxSpeed = 10f,
            DeltaTime = 1f,
            Turbo = false,
            RotationSpeed = 1f
        };

        body.Position = Vector2.Zero;

        // Act
        forwardMovementPhysx.ApplyMovement(body, input);
        world.Step(1f); // Simulate one step in the world (DeltaTime)

        // Assert
        Assert.Equal(Vector2.Zero, body.Position);  // No movement
        Assert.Equal(Vector2.Zero, body.LinearVelocity);  // No velocity
        Assert.Equal(5f, input.CurrentSpeed);  // Speed should remain the same
    }

    [Fact]
    public void ApplyMovement_ShouldCapSpeedAtMaxSpeed()
    {
        // Arrange
        var input = new ForwardMovementPhysxInput
        {
            MoveForward = true,
            CurrentSpeed = 8f,
            Acceleration = 5f,
            MaxSpeed = 10f,
            DeltaTime = 1f,
            Turbo = false,
            RotationSpeed = 1f
        };

        body.Position = Vector2.Zero;

        // Act
        forwardMovementPhysx.ApplyMovement(body, input);
        world.Step(1f); // Simulate one step in the world (DeltaTime)

        // Assert
        Assert.Equal(new Vector2(10f, 0f), body.LinearVelocity);  // Velocity should be capped at MaxSpeed (10)
        Assert.Equal(10f, input.CurrentSpeed);  // Speed should be capped
    }

    [Fact]
    public void ApplyRotation_ShouldRotateLeft_WhenRotateLeftIsTrue()
    {
        // Arrange
        var input = new ForwardMovementPhysxInput
        {
            MoveForward = false,
            CurrentSpeed = 5f,
            Acceleration = 2f,
            MaxSpeed = 10f,
            DeltaTime = 1f,
            Turbo = false,
            RotationSpeed = 1f,
            RotateLeft = true,
            RotateRight = false
        };

        body.Rotation = 0f; // Facing right (East)

        // Act
        forwardMovementPhysx.ApplyRotation(body, input);
        world.Step(1f); // Simulate one step in the world (DeltaTime)

        // Assert
        Assert.Equal(-1f, body.Rotation);  // Rotation should decrease by 1 (rotate left)
    }

    [Fact]
    public void ApplyDeceleration_ShouldApplyDecelerationCorrectly()
    {
        // Arrange
        var input = new ForwardMovementPhysxInput
        {
            MoveForward = true,
            CurrentSpeed = 10f,
            Acceleration = 2f,
            MaxSpeed = 15f,
            DeltaTime = 1f,
            Turbo = false,
            RotationSpeed = 1f,
            Deceleration = 0.5f
        };

        body.Position = Vector2.Zero;

        // Act
        forwardMovementPhysx.ApplyDeceleration(body, input);
        world.Step(1f); // Simulate one step in the world (DeltaTime)

        // Assert
        Assert.Equal(new Vector2(-5f, 0f), body.LinearVelocity);  // Deceleration force should apply in the opposite direction
        Assert.Equal(9.5f, input.CurrentSpeed);  // Speed should be decreased by 0.5
    }
}
