namespace Altruist.Physx.Movement;

using System.Numerics;
using Box2DSharp.Collision.Shapes;
using Box2DSharp.Dynamics;
using Xunit;

public class ForwardMovementPhysxTests
{
        private World world;
        private Body body;
        private MovementPhysx movementPhysx;

        public ForwardMovementPhysxTests()
        {
                // Initialize Box2D world with zero gravity
                world = new World(new Vector2(0f, 0f));

                movementPhysx = new MovementPhysx();

                // Define the body
                var bodyDef = new BodyDef
                {
                        BodyType = BodyType.DynamicBody,
                        Position = new Vector2(0f, 0f),
                        Angle = 0f,              // Facing right (0 radians)
                        LinearDamping = 0f,
                        AngularDamping = 0f,
                        FixedRotation = false
                };

                body = world.CreateBody(bodyDef);

                // Define shape (Box2DSharp uses half-widths)
                var shape = new PolygonShape();
                shape.SetAsBox(0.5f, 0.5f); // Width=1, Height=1

                // Define fixture
                var fixtureDef = new FixtureDef
                {
                        Shape = shape,
                        Density = 1f,
                        Friction = 0f
                };

                body.CreateFixture(fixtureDef);

                // Mass and dynamics reset is handled internally in Box2D when fixture is added
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

                // Act
                var result = movementPhysx.Forward.CalculateMovement(body, input);
                movementPhysx.ApplyMovement(body, result);

                // Simulate one second in 50 small steps (0.02s each)
                for (int i = 0; i < 50; i++)
                {
                        world.Step(0.02f, 8, 3);
                }

                // Assert
                Assert.Equal(new Vector2(52f, 0f), body.LinearVelocity);
                Assert.True(body.GetPosition().X > 52f);
                Assert.Equal(52f, result.CurrentSpeed);
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

                // Act
                var result = movementPhysx.Forward.CalculateMovement(body, input);
                movementPhysx.ApplyMovement(body, result);

                // Assert
                Assert.Equal(Vector2.Zero, body.LinearVelocity);  // No velocity
                world.Step(1f, 8, 3); // Simulate one step in the world (DeltaTime)
                Assert.Equal(Vector2.Zero, body.GetPosition());  // No movement
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

                // Act
                var result = movementPhysx.Forward.CalculateMovement(body, input);
                movementPhysx.ApplyMovement(body, result);

                // Assert
                // Velocity should be capped at MaxSpeed (10)
                Assert.Equal(new Vector2(10f, 0f), body.LinearVelocity);
                // Speed should be capped
                Assert.Equal(10f, result.CurrentSpeed);
                // Simulate one second in 50 small steps (0.02s each)
                for (int i = 0; i < 50; i++)
                {
                        world.Step(0.02f, 8, 3);
                }
                Assert.Equal(new Vector2(10f, 0f), new Vector2((float)Math.Round(body.GetPosition().X, 2), 0));

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
                body.SetTransform(body.GetPosition(), 0f);

                // Act
                var result = movementPhysx.Forward.CalculateMovement(body, input);
                movementPhysx.ApplyMovement(body, result);

                // Assert
                // Rotation should decrease by 1 (rotate left)
                Assert.Equal(-1f, body.GetAngle());
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

                body.BodyType = BodyType.DynamicBody;
                body.LinearDamping = 0f;
                body.SetTransform(Vector2.Zero, 0f);
                body.SetLinearVelocity(new Vector2(10f, 0f));

                // Act
                var result = movementPhysx.Forward.CalculateDeceleration(body, input);
                movementPhysx.ApplyMovement(body, result);
                Assert.Equal(9.5f, result.CurrentSpeed);
        }
}
