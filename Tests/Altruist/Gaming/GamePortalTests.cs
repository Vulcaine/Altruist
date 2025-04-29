/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/


using Altruist.Networking;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;

namespace Altruist.Gaming;


public class AltruistGamePortalTests
{
    private Mock<GamePortalContext> _mockContext;
    private Mock<IWorldPartitioner> _mockPartitioner;
    private Mock<GameWorldCoordinator> _mockCoordinator;
    private Mock<IPlayerService<TestPlayerEntity>> _mockPlayerService;
    private Mock<ILoggerFactory> _mockLoggerFactory;
    private Mock<IAltruistRouter> _mockRouter;
    private Mock<ILogger<TestAltruistGamePortal>> _mockLogger;
    private Mock<ICacheProvider> _mockCache;
    private TestAltruistGamePortal _gamePortal;

    // Common Setup for all tests
    public AltruistGamePortalTests()
    {
        _mockPartitioner = new Mock<IWorldPartitioner>();
        _mockCache = new Mock<ICacheProvider>();
        _mockCoordinator = new Mock<GameWorldCoordinator>(_mockPartitioner.Object, _mockCache.Object);

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



        _mockContext = new Mock<GamePortalContext>(mockAltruistContext.Object, mockProvider.Object);
        _mockPlayerService = new Mock<IPlayerService<TestPlayerEntity>>();
        _mockLoggerFactory = new Mock<ILoggerFactory>();
        _mockLogger = new Mock<ILogger<TestAltruistGamePortal>>();

        _mockLoggerFactory.Setup(p => p.CreateLogger(It.IsAny<string>())).Returns(_mockLogger.Object);
        _mockRouter = SetupRouterMock();  // Set up the router mock
        _mockContext.Setup(p => p.Router).Returns(_mockRouter.Object);

        // Create the portal instance (this will use DI)
        _gamePortal = new TestAltruistGamePortal(_mockContext.Object, _mockCoordinator.Object, _mockPlayerService.Object, _mockLoggerFactory.Object);
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

    private void SetupMockPlayerService(string clientId, TestPlayerEntity? player = null)
    {
        _mockPlayerService.Setup(s => s.GetPlayerAsync(clientId)).ReturnsAsync(player);
        _mockPlayerService.Setup(s => s.DisconnectAsync(clientId)).Returns(Task.CompletedTask);
    }



    private void SetupMockRoomDeletion(string clientId, bool roomIsEmpty)
    {
        if (roomIsEmpty)
        {
            _mockContext.Setup(s => s.DeleteRoomAsync(It.IsAny<string>())).Returns(Task.CompletedTask);
        }
        else
        {
            _mockContext.Setup(s => s.DeleteRoomAsync(It.IsAny<string>())).Verifiable();
        }
    }

    private void SetupMockPlayerServiceNotFound(string clientId)
    {
        _mockPlayerService.Setup(s => s.GetPlayerAsync(clientId)).ReturnsAsync((TestPlayerEntity)null!);
    }

    private void SetupMockRoomService(string clientId, RoomPacket roomMock)
    {
        _mockContext.Setup(s => s.FindRoomForClientAsync(clientId)).ReturnsAsync(roomMock);
        _mockContext.Setup(s => s.SaveRoomAsync(It.IsAny<RoomPacket>())).Returns(Task.CompletedTask);
    }

    private void SetupMockNoRoomService(string clientId)
    {
        _mockContext.Setup(s => s.FindRoomForClientAsync(clientId)).ReturnsAsync((RoomPacket)null!);
    }

    private void VerifyClientSendSuccess(string clientId)
    {
        _mockRouter.Verify(r => r.Client.SendAsync(clientId, It.Is<IPacketBase>(p => p is SuccessPacket)), Times.Once);
    }

    private void VerifyRoomSendAsync(string roomId)
    {
        _mockRouter.Verify(r => r.Room.SendAsync(roomId, It.IsAny<IPacketBase>()), Times.Once);
    }

    private void VerifyClientSendAsync(string clientId)
    {
        _mockRouter.Verify(r => r.Client.SendAsync(clientId, It.IsAny<IPacketBase>()), Times.Once);
    }

    private void VerifyRoomNotDeleted()
    {
        _mockContext.Verify(c => c.DeleteRoomAsync(It.IsAny<string>()), Times.Never);
    }


    private void ArrangeExitGameTests(string clientId, string roomId = "room1", bool playerExists = true)
    {
        var playerMock = playerExists ? new TestPlayerEntity { Name = "Player1" } : null;
        SetupMockPlayerService(clientId, playerMock);

        var roomMock = new RoomPacket { Id = roomId };
        if (playerExists)
        {
            roomMock.AddConnection(clientId);  // Player is in the room
        }
        SetupMockRoomService(clientId, roomMock);
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
        VerifyClientSendAsync(clientId);
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
        VerifyClientSendAsync(clientId);
    }


    [Fact]
    public async Task JoinGameAsync_ShouldSendSuccess_WhenPlayerJoinsSuccessfully()
    {
        // Arrange
        var message = new JoinGamePacket { Name = "Player1" };
        var clientId = "client1";
        var roomMock = new RoomPacket { Id = "room1" };

        _mockContext.Setup(s => s.FindAvailableRoomAsync()).ReturnsAsync(roomMock);

        var playerMock = new TestPlayerEntity { Name = "Player1" };
        _mockPlayerService.Setup(s => s.ConnectById(roomMock.Id, clientId, message.Name, message.WorldIndex ?? 0, message.Position)).ReturnsAsync(playerMock);

        // Act
        await _gamePortal.JoinGameAsync(message, clientId);

        // Assert
        VerifyClientSendSuccess(clientId);
        _mockRouter.Verify(r => r.Synchronize.SendAsync(It.IsAny<ISynchronizedEntity>(), It.IsAny<bool>()), Times.Once);
    }


    [Fact]
    public async Task ExitGameAsync_ShouldSendSuccess_WhenPlayerLeavesSuccessfully()
    {
        // Arrange
        var message = new LeaveGamePacket();
        var clientId = "client1";
        ArrangeExitGameTests(clientId);

        // Act
        await _gamePortal.ExitGameAsync(message, clientId);

        // Assert
        VerifyClientSendSuccess(clientId);
        _mockPlayerService.Verify(s => s.DisconnectAsync(clientId), Times.Once);
        _mockContext.Verify(c => c.SaveRoomAsync(It.IsAny<RoomPacket>()), Times.Once);
        VerifyRoomSendAsync("room1");
    }


    [Fact]
    public async Task ExitGameAsync_ShouldNotDoAnything_WhenPlayerDoesNotExist()
    {
        // Arrange
        var message = new LeaveGamePacket();
        var clientId = "client1";

        SetupMockPlayerServiceNotFound(clientId);

        // Act
        await _gamePortal.ExitGameAsync(message, clientId);

        // Assert
        _mockPlayerService.Verify(s => s.DisconnectAsync(It.IsAny<string>()), Times.Never);
        _mockRouter.Verify(r => r.Client.SendAsync(It.IsAny<string>(), It.IsAny<IPacketBase>()), Times.Never);
    }


    [Fact]
    public async Task ExitGameAsync_ShouldDeleteRoom_WhenRoomIsEmptyAfterPlayerLeaves()
    {
        // Arrange
        var message = new LeaveGamePacket();
        var clientId = "client1";
        var playerMock = new TestPlayerEntity { Name = "Player1" };

        // Mock player service to return a player
        _mockPlayerService.Setup(s => s.GetPlayerAsync(clientId)).ReturnsAsync(playerMock);
        _mockPlayerService.Setup(s => s.DisconnectAsync(clientId)).Returns(Task.CompletedTask);

        // Mock room with just one connection
        var roomMock = new RoomPacket { Id = "room1" };
        roomMock.AddConnection(clientId);  // Player is the only one in the room
        _mockContext.Setup(s => s.FindRoomForClientAsync(clientId)).ReturnsAsync(roomMock);
        _mockContext.Setup(s => s.SaveRoomAsync(It.IsAny<RoomPacket>())).Returns(Task.CompletedTask);

        // Mock that the room becomes empty after removal
        roomMock.RemoveConnection(clientId);  // Remove the player

        // Act
        await _gamePortal.ExitGameAsync(message, clientId);

        // Assert
        _mockRouter.Verify(r => r.Client.SendAsync(clientId, It.Is<IPacketBase>(p => p is SuccessPacket)), Times.Once);
        _mockPlayerService.Verify(s => s.DisconnectAsync(clientId), Times.Once);
        _mockContext.Verify(c => c.SaveRoomAsync(It.IsAny<RoomPacket>()), Times.Once);
        _mockRouter.Verify(r => r.Room.SendAsync(roomMock.Id, It.IsAny<IPacketBase>()), Times.Once);
        _mockContext.Verify(c => c.DeleteRoomAsync(roomMock.Id), Times.Once);  // Ensure the room is deleted
    }

    [Fact]
    public async Task ExitGameAsync_ShouldNotDeleteRoom_WhenRoomIsNotEmpty()
    {
        // Arrange
        var message = new LeaveGamePacket();
        var clientId = "client1";
        var playerMock = new TestPlayerEntity { Name = "Player1" };

        // Mock player service to return a player
        _mockPlayerService.Setup(s => s.GetPlayerAsync(clientId)).ReturnsAsync(playerMock);
        _mockPlayerService.Setup(s => s.DisconnectAsync(clientId)).Returns(Task.CompletedTask);

        // Mock room with multiple connections
        var roomMock = new RoomPacket { Id = "room1" };
        roomMock.AddConnection("client2");  // Another player remains in the room
        _mockContext.Setup(s => s.FindRoomForClientAsync(clientId)).ReturnsAsync(roomMock);
        _mockContext.Setup(s => s.SaveRoomAsync(It.IsAny<RoomPacket>())).Returns(Task.CompletedTask);

        // Act
        await _gamePortal.ExitGameAsync(message, clientId);

        // Assert
        _mockRouter.Verify(r => r.Client.SendAsync(clientId, It.Is<IPacketBase>(p => p is SuccessPacket)), Times.Once);
        _mockPlayerService.Verify(s => s.DisconnectAsync(clientId), Times.Once);
        _mockContext.Verify(c => c.SaveRoomAsync(It.IsAny<RoomPacket>()), Times.Once);
        _mockRouter.Verify(r => r.Room.SendAsync(roomMock.Id, It.IsAny<IPacketBase>()), Times.Once);
        _mockContext.Verify(c => c.DeleteRoomAsync(It.IsAny<string>()), Times.Never);  // Ensure the room is not deleted
    }

    [Fact]
    public async Task ExitGameAsync_ShouldNotSendRoomUpdate_WhenRoomDoesNotExist()
    {
        // Arrange
        var message = new LeaveGamePacket();
        var clientId = "client1";
        var playerMock = new TestPlayerEntity { Name = "Player1" };

        // Mock player service to return a player
        _mockPlayerService.Setup(s => s.GetPlayerAsync(clientId)).ReturnsAsync(playerMock);
        _mockPlayerService.Setup(s => s.DisconnectAsync(clientId)).Returns(Task.CompletedTask);

        // Mock that no room is found for the client
        _mockContext.Setup(s => s.FindRoomForClientAsync(clientId)).ReturnsAsync((RoomPacket)null!);

        // Act
        await _gamePortal.ExitGameAsync(message, clientId);

        // Assert
        _mockRouter.Verify(r => r.Client.SendAsync(clientId, It.Is<IPacketBase>(p => p is SuccessPacket)), Times.Once);
        _mockPlayerService.Verify(s => s.DisconnectAsync(clientId), Times.Once);
        _mockContext.Verify(c => c.SaveRoomAsync(It.IsAny<RoomPacket>()), Times.Never);  // No room to save
        _mockRouter.Verify(r => r.Room.SendAsync(It.IsAny<string>(), It.IsAny<IPacketBase>()), Times.Never);  // No room to send to
    }

    [Fact]
    public async Task HandshakeAsync_ShouldSendHandshakePacket_WithRooms()
    {
        // Arrange
        var clientId = "client1";
        var rooms = new Dictionary<string, RoomPacket>
    {
        { "room1", new RoomPacket { Id = "room1" } },
        { "room2", new RoomPacket { Id = "room2" } }
    };

        // Mock the GetAllRoomsAsync method to return the rooms
        _mockContext.Setup(s => s.GetAllRoomsAsync()).ReturnsAsync(rooms);

        var message = new HandshakePacket("server", null!, clientId); // Just to pass into the method

        // Act
        await _gamePortal.HandshakeAsync(message, clientId);

        // Assert
        _mockRouter.Verify(r => r.Client.SendAsync(clientId, It.Is<HandshakePacket>(p => p.Rooms.Length == rooms.Count)), Times.Once); // Verify the correct number of rooms is sent
    }

    [Fact]
    public async Task HandshakeAsync_ShouldSendHandshakePacket_WithEmptyRoomList_WhenNoRoomsExist()
    {
        // Arrange
        var clientId = "client1";
        var rooms = new Dictionary<string, RoomPacket>();  // No rooms

        _mockContext.Setup(s => s.GetAllRoomsAsync()).ReturnsAsync(rooms);

        var message = new HandshakePacket("server", null!, clientId); // Just to pass into the method

        // Act
        await _gamePortal.HandshakeAsync(message, clientId);

        // Assert
        _mockRouter.Verify(r => r.Client.SendAsync(clientId, It.Is<HandshakePacket>(p => p.Rooms.Length == 0)), Times.Once);
    }

}

// Test Portal that extends the real portal to expose methods for testing
public class TestAltruistGamePortal : AltruistGameSessionPortal<TestPlayerEntity>
{
    public TestAltruistGamePortal(GamePortalContext context, GameWorldCoordinator gameWorld, IPlayerService<TestPlayerEntity> playerService, ILoggerFactory loggerFactory) : base(context, gameWorld, playerService, loggerFactory)
    {
    }

    public override Task Cleanup()
    {
        return base.Cleanup();
    }

    public override async Task JoinGameAsync(JoinGamePacket message, string clientId)
    {
        await base.JoinGameAsync(message, clientId);
    }
}


public class TestPlayerEntity : PlayerEntity
{

}