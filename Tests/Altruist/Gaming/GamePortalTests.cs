using Altruist;
using Altruist.Gaming;
using Microsoft.Extensions.Logging;
using Moq;

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
        var mockEncoder = Mock.Of<IMessageEncoder>();

        // Mock the ClientSender as it is a dependency for RoomSender
        var clientMock = new Mock<ClientSender>(mockStore, mockEncoder);

        // Mock the RoomSender and its methods like SendAsync
        var roomSenderMock = new Mock<RoomSender>(mockStore, mockEncoder, clientMock.Object) { CallBase = true }; // CallBase allows us to use the real implementation for non-mocked methods
        roomSenderMock.Setup(r => r.SendAsync(It.IsAny<string>(), It.IsAny<IPacketBase>())).Returns(Task.CompletedTask); // Mock SendAsync to not do actual work

        // Mock the IAltruistRouter and set up the mock behavior for Client and Room properties
        var routerMock = new Mock<IAltruistRouter>();
        routerMock.Setup(r => r.Client).Returns(clientMock.Object);
        routerMock.Setup(r => r.Room).Returns(roomSenderMock.Object); // Return the mocked RoomSender instance

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

        _mockContext.Setup(p => p.FindAvailableRoom()).ReturnsAsync(roomMock);

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
    public async Task JoinGameAsync_ShouldSendSuccess_WhenPlayerJoins()
    {

        // Arrange
        var message = new JoinGamePacket { Name = "Player1" };
        var clientId = "client1";
        var roomMock = new RoomPacket();

        _mockContext.Setup(s => s.FindAvailableRoom()).ReturnsAsync(roomMock);

        var routerMock = SetupRouterMock();
        _mockContext.Setup(p => p.Router).Returns(routerMock.Object);

        // Act
        await _gamePortal.JoinGameAsync(message, clientId);

        // Assert
        routerMock.Verify(r => r.Room.SendAsync(roomMock.Id, It.IsAny<IPacketBase>()), Times.Once);
    }

    // You can add more tests similarly...
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

    public async Task JoinGameAsync(JoinGamePacket message, string clientId)
    {
        // You can override JoinGameAsync for testing purposes if needed
        await base.JoinGameAsync(message, clientId);
    }
}
