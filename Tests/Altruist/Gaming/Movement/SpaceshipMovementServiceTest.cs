using System.Numerics;
using Altruist.Physx;
using Box2DSharp.Dynamics;
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

    private readonly World _world;

    public ForwardSpaceshipMovementServiceTests()
    {
        _portalContextMock = new Mock<IPortalContext>();
        _playerServiceMock = new Mock<IPlayerService<TestSpaceship>>();
        _cacheProviderMock = new Mock<ICacheProvider>();
        _movementPhysxMock = new Mock<MovementPhysx>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();

        _movementService = new TestSpaceshipMovementService(
            _playerServiceMock.Object,
            _movementPhysxMock.Object,
            _cacheProviderMock.Object,
            _loggerFactoryMock.Object
        );
        _world = new World(Vector2.Zero);
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

        spaceship.CalculatePhysxBody(_world);

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

        // IMPORTANT!!! we can't assert movement as the movement update will happen in the engine
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

        spaceship.CalculatePhysxBody(_world);

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
        Assert.True(result.TurboFuel > 10);
    }

    private class TestSpaceshipMovementService : ForwardSpacehipMovementService<TestSpaceship>
    {
        public TestSpaceshipMovementService(IPlayerService<TestSpaceship> playerService, MovementPhysx movementPhysx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory)
            : base(playerService, movementPhysx, cacheProvider, loggerFactory) { }
    }
}

public class TestSpaceship : Spaceship
{
    public override string SysId { get; set; } = "testId";
}
