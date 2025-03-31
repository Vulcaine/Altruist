using Moq;
using StackExchange.Redis;

namespace Altruist.Redis;

public class RedisSocketClientSenderTests
{
    private readonly Mock<IConnectionStore> _mockStore;
    private readonly Mock<ICodec> _mockCodec;
    private readonly Mock<IConnectionMultiplexer> _mockMux;
    private readonly Mock<ISubscriber> _mockRedisPublisher;
    private readonly Mock<ClientSender> _mockClientSender;
    private readonly Mock<IDatabase> _mockRedisDatabase;
    private readonly RedisSocketClientSender _redisSocketClientSender;

    public RedisSocketClientSenderTests()
    {
        _mockStore = new Mock<IConnectionStore>();
        _mockCodec = new Mock<ICodec>();
        _mockMux = new Mock<IConnectionMultiplexer>();
        _mockRedisPublisher = new Mock<ISubscriber>();
        _mockRedisDatabase = new Mock<IDatabase>();

        // Mock ClientSender and its SendAsync method
        _mockClientSender = new Mock<ClientSender>(_mockStore.Object, _mockCodec.Object);
        _mockClientSender.Setup(c => c.SendAsync(It.IsAny<string>(), It.IsAny<IPacketBase>()))
                         .Returns(Task.CompletedTask); // Mock SendAsync method

        _mockMux.Setup(m => m.GetSubscriber(It.IsAny<object?>())).Returns(_mockRedisPublisher.Object);
        _mockMux.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>())).Returns(_mockRedisDatabase.Object);

        // Set up the RedisSocketClientSender instance
        _redisSocketClientSender = new RedisSocketClientSender(
            _mockStore.Object,
            _mockCodec.Object,
            _mockMux.Object,
            _mockClientSender.Object
        );
    }

    [Fact]
    public async Task SendAsync_ShouldCallUnderlyingSend_WhenSocketIsConnected()
    {
        // Arrange
        var clientId = "client1";
        var message = new JoinGamePacket(); // Example message
        var mockConnection = new Mock<IConnection>();
        mockConnection.Setup(c => c.IsConnected).Returns(true);

        _mockStore.Setup(s => s.GetConnectionAsync(clientId)).ReturnsAsync(mockConnection.Object);

        // Act
        await _redisSocketClientSender.SendAsync(clientId, message);

        // Assert
        _mockClientSender.Verify(c => c.SendAsync(clientId, message), Times.Once);
        _mockRedisPublisher.Verify(r => r.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), CommandFlags.FireAndForget), Times.Never);
    }

    [Fact]
    public async Task SendAsync_ShouldPushToRedis_WhenSocketIsNotConnected()
    {
        // Arrange
        var clientId = "client1";
        var message = new JoinGamePacket(); // Example message
        var mockConnection = new Mock<IConnection>();
        mockConnection.Setup(c => c.IsConnected).Returns(false);

        _mockStore.Setup(s => s.GetConnectionAsync(clientId)).ReturnsAsync(mockConnection.Object);
        _mockCodec.Setup(e => e.Encoder.Encode(It.IsAny<IPacketBase>())).Returns([10]);

        // Act
        await _redisSocketClientSender.SendAsync(clientId, message);

        // Assert
        _mockMux.Verify(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>()).ListLeftPushAsync(IngressRedis.MessageQueue, It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()), Times.Once);
        _mockRedisPublisher.Verify(r => r.PublishAsync(It.IsAny<RedisChannel>(), "", CommandFlags.FireAndForget), Times.Once);
    }

    [Fact]
    public async Task SendAsync_ShouldNotPushToRedis_WhenSocketIsConnected()
    {
        // Arrange
        var clientId = "client1";
        var message = new JoinGamePacket(); // Example message
        var mockConnection = new Mock<IConnection>();
        mockConnection.Setup(c => c.IsConnected).Returns(true);

        _mockStore.Setup(s => s.GetConnectionAsync(clientId)).ReturnsAsync(mockConnection.Object);

        // Act
        await _redisSocketClientSender.SendAsync(clientId, message);

        // Assert
        _mockMux.Verify(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object?>()).ListLeftPushAsync(IngressRedis.MessageQueue, It.IsAny<RedisValue>(), It.IsAny<When>(), It.IsAny<CommandFlags>()), Times.Never);
        _mockRedisPublisher.Verify(r => r.PublishAsync(It.IsAny<RedisChannel>(), It.IsAny<RedisValue>(), CommandFlags.FireAndForget), Times.Never);
    }
}
