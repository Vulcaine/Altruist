using Altruist.Physx;
using FarseerPhysics.Dynamics;
using Microsoft.Extensions.Logging;
using Moq;

namespace Altruist.Gaming.Movement;

public class ForwardSpaceshipMovementServiceTests
{
    private readonly Mock<IPortalContext> _portalContextMock;
    private readonly Mock<IPlayerService<TestSpaceship>> _playerServiceMock;
    private readonly Mock<ICacheProvider> _cacheProviderMock;
    private readonly Mock<MovementPhysx> _movementPhysxMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;

    private readonly TestSpaceshipMovementService _movementService;

    public ForwardSpaceshipMovementServiceTests()
    {
        _portalContextMock = new Mock<IPortalContext>();
        _playerServiceMock = new Mock<IPlayerService<TestSpaceship>>();
        _cacheProviderMock = new Mock<ICacheProvider>();
        _movementPhysxMock = new Mock<MovementPhysx>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();

        _movementService = new TestSpaceshipMovementService(
            _portalContextMock.Object,
            _playerServiceMock.Object,
            _movementPhysxMock.Object,
            _cacheProviderMock.Object,
            _loggerFactoryMock.Object
        );
    }

    [Fact]
    public async Task MovePlayerAsync_WithTurbo_DecreasesFuelAndMoves()
    {
        // Arrange
        var spaceship = new TestSpaceship
        {
            SysId = "ship123",
            Position = [0f, 0f],
            Rotation = 0f,
            Acceleration = 1f,
            MaxSpeed = 5f,
            CurrentSpeed = 0f,
            TurboFuel = 10,
            MaxTurboFuel = 20,
            RotationSpeed = 0.1f
        };

        var movementPacket = new ForwardMovementPacket
        {
            MoveUp = true,
            RotateLeft = true,
            Turbo = true
        };

        _playerServiceMock
            .Setup(x => x.FindEntityAsync(spaceship.SysId))
            .ReturnsAsync(spaceship);

        _cacheProviderMock
            .Setup(x => x.SaveAsync(spaceship.SysId, spaceship, ""))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _movementService.MovePlayerAsync(spaceship.SysId, movementPacket);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Moving);
        Assert.Equal(9, result.TurboFuel); // Should decrease by 1
        Assert.True(result.CurrentSpeed > 0); // Should have accelerated
        Assert.NotEqual(0f, result.Position[0]); // Should have moved
        Assert.NotEqual(0f, result.Position[1]); // Should have moved
        Assert.True(result.Rotation != 0); // Should have rotated
    }

    [Fact]
    public async Task MovePlayerAsync_WithoutTurbo_RecoversFuel()
    {
        // Arrange
        var spaceship = new TestSpaceship
        {
            SysId = "ship124",
            Position = [0f, 0f],
            Rotation = 0f,
            Acceleration = 1f,
            MaxSpeed = 5f,
            CurrentSpeed = 0f,
            TurboFuel = 10,
            MaxTurboFuel = 20,
            RotationSpeed = 0.1f
        };

        var movementPacket = new ForwardMovementPacket
        {
            MoveUp = true,
            RotateLeft = false,
            Turbo = false
        };

        _playerServiceMock
            .Setup(x => x.FindEntityAsync(spaceship.SysId))
            .ReturnsAsync(spaceship);

        _cacheProviderMock
            .Setup(x => x.SaveAsync(spaceship.SysId, spaceship, ""))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _movementService.MovePlayerAsync(spaceship.SysId, movementPacket);

        // Assert
        Assert.NotNull(result);
        Assert.True(result.Moving);
        Assert.True(result.TurboFuel > 10); // Should have slightly recovered fuel
    }

    // Test classes
    private class TestSpaceshipMovementService : ForwardSpacehipMovementService<TestSpaceship>
    {
        public TestSpaceshipMovementService(IPortalContext context, IPlayerService<TestSpaceship> playerService, MovementPhysx movementPhysx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory)
            : base(context, playerService, movementPhysx, cacheProvider, loggerFactory) { }

        protected override void ApplyDeceleration(Body body, TestSpaceship entity)
        {
            _movementPhysx.Forward.ApplyDeceleration(body, new ForwardMovementPhysxInput
            {
                CurrentSpeed = entity.CurrentSpeed,
                Deceleration = entity.Deceleration,
                DeltaTime = 1.0f,
                MaxSpeed = entity.MaxSpeed,
                MoveForward = false,
                Turbo = false
            });
        }
    }
}

public class TestSpaceship : Spaceship
{
    public override string SysId { get; set; } = "testId";
}
