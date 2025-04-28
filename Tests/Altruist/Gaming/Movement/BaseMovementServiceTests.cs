
using Altruist.Physx;
using FarseerPhysics.Dynamics;
using Microsoft.Extensions.Logging;
using Microsoft.Xna.Framework;
using Moq;

namespace Altruist.Gaming.Movement;

public class BaseMovementServiceTests
{
    private readonly Mock<IPortalContext> _portalContextMock;
    private readonly Mock<IPlayerService<TestPlayer>> _playerServiceMock;
    private readonly Mock<ICacheProvider> _cacheProviderMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly Mock<ILogger> _loggerMock;

    private readonly TestMovementService _movementService;

    public BaseMovementServiceTests()
    {
        _portalContextMock = new Mock<IPortalContext>();
        _playerServiceMock = new Mock<IPlayerService<TestPlayer>>();
        _cacheProviderMock = new Mock<ICacheProvider>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _loggerMock = new Mock<ILogger>();
        var movementPhysxMock = new Mock<MovementPhysx>();

        _loggerFactoryMock.Setup(f => f.CreateLogger("")).Returns(_loggerMock.Object);

        _movementService = new TestMovementService(
            _portalContextMock.Object,
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

        var body = new Body(new World(Vector2.Zero))
        {
            Rotation = 0f
        };

        // Act
        _movementService.TestApplyDeceleration(body, player);

        // Assert
        Assert.Equal(4f, player.CurrentSpeed);
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

        // Act
        _movementService.TestClampSpeed(player);

        // Assert
        Assert.Equal(5f, player.CurrentSpeed);
    }

    // Test Classes
    private class TestMovementService : BaseMovementService<TestPlayer>
    {
        public TestMovementService(IPortalContext context, IPlayerService<TestPlayer> playerService, MovementPhysx movementPhysx, ICacheProvider cacheProvider, ILoggerFactory loggerFactory)
            : base(context, playerService, movementPhysx, cacheProvider, loggerFactory) { }

        protected override void ApplyRotation(Body body, TestPlayer entity, IMovementPacket input) { }
        protected override void ApplyMovement(Body body, TestPlayer entity, IMovementPacket input) { }

        public void TestApplyDeceleration(Body body, TestPlayer entity) => ApplyDeceleration(body, entity);
        public void TestClampSpeed(TestPlayer entity) => ClampSpeed(entity);

        protected override void ApplyDeceleration(Body body, TestPlayer entity)
        {

        }
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
