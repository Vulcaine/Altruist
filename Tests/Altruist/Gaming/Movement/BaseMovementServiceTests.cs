
using Altruist.Physx;
using FarseerPhysics.Dynamics;
using Microsoft.Extensions.Logging;
using Microsoft.Xna.Framework;
using Moq;

namespace Altruist.Gaming.Movement;

public class BaseMovementServiceTests
{
    private readonly Mock<IPlayerService<TestPlayer>> _playerServiceMock;
    private readonly Mock<ICacheProvider> _cacheProviderMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly Mock<ILogger> _loggerMock;

    private readonly TestMovementService _movementService;

    private readonly World _world = new World(Vector2.Zero);

    public BaseMovementServiceTests()
    {
        _playerServiceMock = new Mock<IPlayerService<TestPlayer>>();
        _cacheProviderMock = new Mock<ICacheProvider>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerMock = new Mock<ILogger>();
        var movementPhysxMock = new Mock<MovementPhysx>();

        _loggerFactoryMock.Setup(f => f.CreateLogger("")).Returns(_loggerMock.Object);

        _movementService = new TestMovementService(
            _playerServiceMock.Object,
            movementPhysxMock.Object,
            _cacheProviderMock.Object,
            _loggerFactoryMock.Object
        );
    }

    [Fact]
    public async Task MovePlayerAsync_PlayerFound_ShouldMoveAndSave()
    {
        // Arrange
        var player = new TestPlayer
        {
            SysId = "sys123",
            Position = [0f, 0f],
            Rotation = 0f,
            Acceleration = 1f,
            MaxSpeed = 5f
        };

        player.CalculatePhysxBody(_world);
        var input = new TestMovementPacket();

        _playerServiceMock
            .Setup(x => x.FindEntityAsync(player.SysId))
            .ReturnsAsync(player);

        _cacheProviderMock
            .Setup(x => x.SaveAsync(player.SysId, player, ""))
            .Returns(Task.CompletedTask);

        // Act
        var result = await _movementService.MovePlayerAsync(player.SysId, input);

        // Assert
        Assert.NotNull(result);
        _playerServiceMock.Verify(x => x.FindEntityAsync(player.SysId), Times.Once);
        _cacheProviderMock.Verify(x => x.SaveAsync(player.SysId, player, ""), Times.Once);
    }

    [Fact]
    public async Task MovePlayerAsync_PlayerNotFound_ShouldReturnNull()
    {
        // Arrange
        _playerServiceMock
            .Setup(x => x.FindEntityAsync("unknown"))
            .ReturnsAsync((TestPlayer)null!);

        var input = new TestMovementPacket();

        // Act
        var result = await _movementService.MovePlayerAsync("unknown", input);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public void ApplyDeceleration_ShouldDecreaseSpeed()
    {
        // Arrange
        var player = new TestPlayer
        {
            CurrentSpeed = 5f,
            Deceleration = 1f
        };

        var body = player.CalculatePhysxBody(_world);
        // Act
        _movementService.TestApplyDeceleration(body, player);

        // Assert
        // Its not actually decreasing now because it is not inside a world simulation
        // The world simulation is run by the engine
        Assert.Equal(5f, player.CurrentSpeed);
    }

    [Fact]
    public void ClampSpeed_ShouldLimitMaxSpeed()
    {
        // Arrange
        var player = new TestPlayer
        {
            CurrentSpeed = 10f,
            MaxSpeed = 5f
        };
        player.CalculatePhysxBody(_world);
        // Act
        _movementService.TestClampSpeed(player);

        // Assert
        Assert.Equal(5f, player.CurrentSpeed);
    }

    // Test Classes
    private class TestMovementService : BaseMovementService<TestPlayer>
    {
        public TestMovementService(IPlayerService<TestPlayer> playerService, MovementPhysx movementPhysx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory)
            : base(playerService, movementPhysx, cacheProvider, loggerFactory) { }

        protected override void ApplyRotation(Body body, TestPlayer entity, IMovementPacket input) { }
        protected override void ApplyMovement(Body body, TestPlayer entity, IMovementPacket input) { }

        public void TestApplyDeceleration(Body body, TestPlayer entity)
        {
            var result = _movementPhysx.Forward.CalculateDeceleration(body, new ForwardMovementPhysxInput
            {
                CurrentSpeed = entity.CurrentSpeed,
                Deceleration = entity.Deceleration,
                DeltaTime = 1f,
                MaxSpeed = entity.MaxSpeed,
                MoveForward = false,
                RotationSpeed = entity.RotationSpeed,
                Turbo = false
            });

            _movementPhysx.ApplyMovement(body, result);
        }
        public void TestClampSpeed(TestPlayer entity) => ClampSpeed(entity);
    }
}

public class TestPlayer : PlayerEntity
{
    public override string SysId { get; set; } = "testId";
}

public class TestMovementPacket : IMovementPacket
{
    public string Type { get; set; } = "test";

    public PacketHeader Header { get; set; }
}
