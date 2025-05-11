using Altruist.Gaming.Movement;
using Altruist.Networking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Altruist.Gaming;

public class AltruistMovementPortalTests
{
    private readonly Mock<MovementPortalContext> _contextMock;
    private readonly Mock<IPlayerService<TestPlayer>> _playerServiceMock;
    private readonly Mock<IMovementService<TestPlayer>> _movementServiceMock;
    private readonly Mock<ILoggerFactory> _loggerFactoryMock;
    private readonly Mock<IAltruistRouter> _routerMock;

    private readonly TestMovementPortal _portal;

    public AltruistMovementPortalTests()
    {

        var mockAltruistContext = new Mock<IAltruistContext>();
        var mockProvider = new Mock<IServiceProvider>();
        var connStore = new Mock<IConnectionStore>();
        var router = new Mock<IAltruistRouter>();
        var cache = new Mock<ICacheProvider>();
        var cursor = new Mock<IPlayerCursorFactory>();

        mockProvider
        .Setup(s => s.GetService(typeof(IConnectionStore)))
        .Returns(connStore.Object);

        mockProvider
        .Setup(s => s.GetService(typeof(IAltruistRouter)))
        .Returns(router.Object);

        mockProvider
        .Setup(s => s.GetService(typeof(ICacheProvider)))
        .Returns(cache.Object);

        mockProvider
        .Setup(s => s.GetService(typeof(IPlayerCursorFactory)))
        .Returns(cursor.Object);

        _contextMock = new Mock<MovementPortalContext>(mockAltruistContext.Object, mockProvider.Object);

        _playerServiceMock = new Mock<IPlayerService<TestPlayer>>();
        _movementServiceMock = new Mock<IMovementService<TestPlayer>>();
        _loggerFactoryMock = new Mock<ILoggerFactory>();
        _routerMock = new Mock<IAltruistRouter>();

        var codecMock = new Mock<ICodec>();
        var connectionStoreMock = new Mock<IConnectionStore>();
        var clientSenderMock = new Mock<ClientSender>(connectionStoreMock.Object, codecMock.Object);
        var broadcastMock = new Mock<BroadcastSender>(connectionStoreMock.Object, clientSenderMock.Object);
        var clientSyncMock = new Mock<IClientSynchronizator>();
        clientSyncMock.Setup(s => s.SendAsync(It.IsAny<ISynchronizedEntity>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        _routerMock.Setup(s => s.Synchronize).Returns(clientSyncMock.Object);
        _routerMock.Setup(s => s.Client).Returns(clientSenderMock.Object);

        _contextMock.Setup(c => c.Router).Returns(_routerMock.Object);

        _portal = new TestMovementPortal(
            _contextMock.Object,
            _playerServiceMock.Object,
            _movementServiceMock.Object,
            _loggerFactoryMock.Object
        );
    }

    [Fact]
    public async Task SyncMovement_PlayerExists_ShouldMove()
    {
        // Arrange
        var clientId = "player1";
        var movementPacket = new TestMovementPacket();
        var playerEntity = new TestPlayer { Id = clientId };

        _playerServiceMock
            .Setup(p => p.GetPlayerAsync(clientId))
            .ReturnsAsync(playerEntity);

        _movementServiceMock
            .Setup(m => m.MovePlayerAsync(clientId, movementPacket))
            .ReturnsAsync(playerEntity);

        _routerMock.Setup(r => r.Synchronize.SendAsync(It.IsAny<ISynchronizedEntity>(), It.IsAny<bool>())).Returns(Task.CompletedTask);

        // Act
        await _portal.SyncMovement(movementPacket, clientId);

        // Assert
        _playerServiceMock.Verify(p => p.GetPlayerAsync(clientId), Times.Once);
        _movementServiceMock.Verify(m => m.MovePlayerAsync(clientId, movementPacket), Times.Once);
    }

    [Fact]
    public async Task SyncMovement_PlayerNotFound_ShouldSendFailure()
    {
        // Arrange
        var clientId = "unknownPlayer";
        var movementPacket = new TestMovementPacket();

        _playerServiceMock
            .Setup(p => p.GetPlayerAsync(clientId))
            .ReturnsAsync((TestPlayer)null!);

        _routerMock.Setup(r => r.Client.SendAsync(
                clientId,
                It.IsAny<IPacketBase>()
            ))
            .Returns(Task.CompletedTask);

        // Act
        await _portal.SyncMovement(movementPacket, clientId);

        // Assert
        _playerServiceMock.Verify(p => p.GetPlayerAsync(clientId), Times.Once);
        _movementServiceMock.Verify(m => m.MovePlayerAsync(It.IsAny<string>(), It.IsAny<TestMovementPacket>()), Times.Never);
        _routerMock.Verify(r => r.Client.SendAsync(
            clientId,
            It.Is<IPacketBase>(o => o != null)
        ), Times.Once);
    }

    // Helper Classes
    private class TestMovementPortal : AltruistMovementPortal<TestPlayer, TestMovementPacket>
    {
        public TestMovementPortal(MovementPortalContext context, IPlayerService<TestPlayer> playerService, IMovementService<TestPlayer> movementService, ILoggerFactory loggerFactory)
            : base(context, playerService, movementService, loggerFactory) { }
    }

}


public class TestPlayer : PlayerEntity
{
    public string Id { get; set; } = "";
}

public class TestMovementPacket : IMovementPacket
{
    public PacketHeader Header { get; set; }
    public string Type { get; set; } = "TestMovementPacket";
}