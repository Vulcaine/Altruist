using Altruist.Networking;
using Microsoft.Extensions.Logging;
using Moq;

namespace Altruist.Gaming;

public class AltruistGamePortalTests
{
    private Mock<IPortalContext> _mockContext;
    private Mock<IPlayerService<PlayerEntity>> _mockPlayerService;
    private Mock<ILoggerFactory> _mockLoggerFactory;
    private Mock<IAltruistRouter> _mockRouter;
    private Mock<ILogger<AltruistGamePortal<PlayerEntity>>> _mockLogger;
    private TestAltruistGamePortal _gamePortal;

    // Common Setup for all tests
    public AltruistGamePortalTests()
    {
        _mockContext = new Mock<IPortalContext>();
        _mockPlayerService = new Mock<IPlayerService<PlayerEntity>>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger<AltruistGamePortal<PlayerEntity>>>();

        _mockContext.Setup(c => c.GetPlayerService<PlayerEntity>()).Returns(_mockPlayerService.Object);
        _mockLoggerFactory.Setup(p => p.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);
        _mockRouter = SetupRouterMock();  // Set up the router mock
        _mockContext.Setup(p => p.Router).Returns(_mockRouter.Object);

        // Create the portal instance (this will use DI)
        _gamePortal = new TestAltruistGamePortal(_mockContext.Object, _mockLoggerFactory.Object);
    }

    // Common Setup for Router Mocks
    private Mock<IAltruistRouter> SetupRouterMock()
    {
        // Mock the necessary dependencies for RoomSender and ClientSender
        var mockStore = Mock.Of<IConnectionStore>();
        var mockCodec = Mock.Of<ICodec>();

        // Mock the ClientSender as it is a dependency for RoomSender
        var clientMock = new Mock<ClientSender>(mockStore, mockCodec);

        // Mock the BroadcastSender with the necessary dependencies
        var broadcastMock = new Mock<BroadcastSender>(mockStore, clientMock.Object);

        // Mock the ClientSynchronizator with BroadcastSender
        var clientSyncMock = new Mock<ClientSynchronizator>(broadcastMock.Object);

        // Mock the RoomSender and its methods like SendAsync
        var roomSenderMock = new Mock<RoomSender>(mockStore, mockCodec, clientMock.Object) { CallBase = true };
        roomSenderMock.Setup(r => r.SendAsync(It.IsAny<string>(), It.IsAny<IPacketBase>())).Returns(Task.CompletedTask);

        // Mock the IAltruistRouter and set up the mock behavior for Client and Room properties
        var routerMock = new Mock<IAltruistRouter>();
        routerMock.Setup(r => r.Client).Returns(clientMock.Object);
        routerMock.Setup(r => r.Room).Returns(roomSenderMock.Object);
        routerMock.Setup(r => r.Synchronize).Returns(clientSyncMock.Object);
        routerMock.Setup(r => r.Broadcast).Returns(broadcastMock.Object);

        return routerMock;
    }


    [Fact]
    public async Task Cleanup_ShouldCallPlayerServiceCleanup()
    {
        // Act
        await _gamePortal.Cleanup();

        // Assert
        _mockPlayerService.Verify(s => s.Cleanup(), Times.Once);
    }

    [Fact]
    public async Task JoinGameAsync_ShouldSendJoinFailed_WhenRoomIsFull()
    {
        // Arrange
        var message = new JoinGamePacket { Name = "Player1" };
        var clientId = "client1";

        // Mock the RoomPacket struct
        var roomMock = new RoomPacket();
        var ids = new HashSet<string>();
        for (int i = 0; i < roomMock.MaxCapactiy; i++)
        {
            ids.Add($"connectionId{i}");
        }
        roomMock.ConnectionIds = ids;

        _mockContext.Setup(p => p.FindAvailableRoomAsync()).ReturnsAsync(roomMock);

        // Act
        await _gamePortal.JoinGameAsync(message, clientId);

        // Assert
        _mockRouter.Verify(r => r.Client.SendAsync(clientId, It.IsAny<IPacketBase>()), Times.Once);
    }

    [Fact]
    public async Task JoinGameAsync_ShouldSendJoinFailed_WhenUsernameIsEmpty()
    {
        // Arrange
        var message = new JoinGamePacket { Name = "" };  // Empty name
        var clientId = "client1";

        // Act
        await _gamePortal.JoinGameAsync(message, clientId);

        // Assert
        _mockRouter.Verify(r => r.Client.SendAsync(clientId, It.IsAny<IPacketBase>()), Times.Once);
    }

    [Fact]
    public async Task JoinGameAsync_ShouldSendJoinFailed_WhenRoomNotFound()
    {
        // Arrange
        var message = new JoinGamePacket { Name = "Player1", RoomId = "non-existing-room" };
        var clientId = "client1";

        _mockContext.Setup(s => s.GetRoomAsync(message.RoomId)).ReturnsAsync((RoomPacket?)null);

        // Act
        await _gamePortal.JoinGameAsync(message, clientId);

        // Assert
        _mockRouter.Verify(r => r.Client.SendAsync(clientId, It.IsAny<IPacketBase>()), Times.Once);
    }

    [Fact]
    public async Task JoinGameAsync_ShouldSendJoinFailed_WhenPlayerAlreadyInGame()
    {
        // Arrange
        var message = new JoinGamePacket { Name = "Player1" };
        var clientId = "client1";
        var roomMock = new RoomPacket();
        roomMock.ConnectionIds.Add(clientId); // Player is already in room

        _mockContext.Setup(s => s.FindAvailableRoomAsync()).ReturnsAsync(roomMock);

        // Act
        await _gamePortal.JoinGameAsync(message, clientId);

        // Assert
        _mockRouter.Verify(r => r.Client.SendAsync(clientId, It.IsAny<IPacketBase>()), Times.Once);
    }

    [Fact]
    public async Task JoinGameAsync_ShouldSendSuccess_WhenPlayerJoinsSuccessfully()
    {
        // Arrange
        var message = new JoinGamePacket { Name = "Player1" };
        var clientId = "client1";
        var roomMock = new RoomPacket { Id = "room1" };

        _mockContext.Setup(s => s.FindAvailableRoomAsync()).ReturnsAsync(roomMock);

        var playerMock = new PlayerEntity { Name = "Player1" };
        _mockPlayerService.Setup(s => s.ConnectById(roomMock.Id, clientId, message.Name, message.Position)).ReturnsAsync(playerMock);

        // Act
        await _gamePortal.JoinGameAsync(message, clientId);

        // Assert
        _mockRouter.Verify(r => r.Client.SendAsync(clientId, It.IsAny<IPacketBase>()), Times.Once);
        _mockRouter.Verify(r => r.Synchronize.SendAsync(It.IsAny<ISynchronizedEntity>()), Times.Once);
    }
}

// Test Portal that extends the real portal to expose methods for testing
public class TestAltruistGamePortal : AltruistGamePortal<PlayerEntity>
{
    public TestAltruistGamePortal(IPortalContext context, ILoggerFactory loggerFactory)
        : base(context, loggerFactory)
    { }

    public override Task Cleanup()
    {
        return base.Cleanup();
    }

    public override async Task JoinGameAsync(JoinGamePacket message, string clientId)
    {
        await base.JoinGameAsync(message, clientId);
    }
}
