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
            CurrentSpeed = 50f,
            Acceleration = 2f,
            MaxSpeed = 300f,
            DeltaTime = 1f,
            Turbo = false,
            RotationSpeed = 1f
        };
        body.BodyType = BodyType.Dynamic;
        body.Mass = 1;
        body.LinearDamping = 0f;
        body.AngularDamping = 0f;
        body.Friction = 0f;
        body.Rotation = 0f; // Facing right (East)
        body.Position = Vector2.Zero;
        body.ResetMassData();
        body.ResetDynamics();

        // Act
        var result = forwardMovementPhysx.ApplyMovement(body, input);

        // Assert
        Assert.Equal(new Vector2(52f, 0f), body.LinearVelocity);
        world.Step(1f); // Simulate one step in the world (DeltaTime) 
                        // LinearVelocity should match velocity direction
                        // Position = 5 + (2 * 1) (based on speed and acceleration)

        // Velocity = currentspeed + (acceleration * turboFactor)
        Assert.Equal(new Vector2(54f, 0f), body.Position);
        Assert.Equal(52f, result.CurrentSpeed);  // Speed after acceleration
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

        body.BodyType = BodyType.Dynamic;
        body.Position = Vector2.Zero;

        // Act
        var result = forwardMovementPhysx.ApplyMovement(body, input);

        // Assert
        Assert.Equal(Vector2.Zero, body.LinearVelocity);  // No velocity
        world.Step(1f); // Simulate one step in the world (DeltaTime)
        Assert.Equal(Vector2.Zero, body.Position);  // No movement
        Assert.Equal(5f, result.CurrentSpeed);  // Speed should remain the same
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

        body.BodyType = BodyType.Dynamic;
        body.Position = Vector2.Zero;

        // Act
        var result = forwardMovementPhysx.ApplyMovement(body, input);
        // Assert
        // Velocity should be capped at MaxSpeed (10)
        Assert.Equal(new Vector2(10f, 0f), body.LinearVelocity);
        // Speed should be capped
        Assert.Equal(10f, result.CurrentSpeed);
        world.Step(1f);
        Assert.Equal(new Vector2(12f, 0f), body.Position);
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

        // Facing right (East)
        body.Rotation = 0f;

        // Act
        forwardMovementPhysx.ApplyRotation(body, input);

        // Assert
        // Rotation should decrease by 1 (rotate left)
        Assert.Equal(-1f, body.Rotation);
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

        body.BodyType = BodyType.Dynamic;
        body.LinearDamping = 0f;
        body.Friction = 0f;
        body.Position = Vector2.Zero;
        body.LinearVelocity = new Vector2(10f, 0f); // some initial velocity

        // Act
        var result = forwardMovementPhysx.ApplyDeceleration(body, input);

        world.Step(0.4f);

        // Assert
        // 10 + (-5) = 5
        Assert.Equal(new Vector2(5f, 0f), body.LinearVelocity);
        Assert.Equal(9.5f, result.CurrentSpeed);
    }
}
