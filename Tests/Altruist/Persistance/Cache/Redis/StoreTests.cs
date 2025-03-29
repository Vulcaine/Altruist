using System.Net;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Altruist;
using Altruist.Redis;
using Moq;
using StackExchange.Redis;

public class RedisCacheProviderTests
{
    private readonly Mock<IConnectionMultiplexer> _mockMultiplexer;
    private readonly Mock<IDatabase> _mockDatabase;
    private readonly Mock<IBatch> _mockDatabaseBatch;
    private readonly Mock<IServer> _mockServer;
    private readonly RedisCacheProvider _cacheProvider;

    private readonly RedisServiceConfiguration _configuration = (RedisCacheServiceToken.Instance.Configuration as RedisServiceConfiguration)!;

    public RedisCacheProviderTests()
    {
        _mockMultiplexer = new Mock<IConnectionMultiplexer>();
        _mockDatabase = new Mock<IDatabase>();
        _mockServer = new Mock<IServer>();
        _mockDatabaseBatch = new Mock<IBatch>();

        _mockMultiplexer.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>())).Returns(_mockDatabase.Object);
        _mockMultiplexer.Setup(m => m.GetServer(It.IsAny<EndPoint>(), It.IsAny<object>())).Returns(_mockServer.Object);
        _mockMultiplexer.Setup(m => m.GetEndPoints(It.IsAny<bool>())).Returns([new DnsEndPoint("localhost", 6379)]);
        _mockDatabase.Setup(m => m.Multiplexer).Returns(_mockMultiplexer.Object);

        _configuration.AddDocument<TestEntity>();
        _cacheProvider = new RedisCacheProvider(_mockMultiplexer.Object);
    }

    private class TestEntity : IStoredModel
    {
        public string GenId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Test";
        public string Type { get; set; } = "TestEntity";
    }

    [Fact]
    public async Task SaveAsync_ShouldSaveEntityToRedis()
    {
        // Arrange
        var testEntity = new TestEntity { GenId = "123", Name = "Test" };
        string key = "123";
        string expectedJson = JsonSerializer.Serialize(testEntity);

        _mockDatabase.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), null, false, When.Always
        , CommandFlags.None))
                     .ReturnsAsync(true);

        // Act
        await _cacheProvider.SaveAsync(key, testEntity);

        // Assert
        _mockDatabase.Verify(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), null, false, When.Always
        , CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnEntity_WhenExistsInRedis()
    {
        // Arrange
        var testEntity = new TestEntity { GenId = "123", Name = "Test" };
        string key = "123";
        string json = JsonSerializer.Serialize(testEntity);

        _mockDatabase.Setup(db => db.StringGetAsync("TestEntity:123", CommandFlags.None)).ReturnsAsync(json);

        // Act
        var result = await _cacheProvider.GetAsync<TestEntity>(key);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(testEntity.Name, result.Name);
        _mockDatabase.Verify(db => db.StringGetAsync("TestEntity:123", CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenKeyDoesNotExist()
    {
        // Arrange
        string key = "123";
        _mockDatabase.Setup(db => db.StringGetAsync(key, CommandFlags.None)).ReturnsAsync(RedisValue.Null);

        // Act
        var result = await _cacheProvider.GetAsync<TestEntity>(key);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveAsync_ShouldDeleteEntityFromRedis()
    {
        // Arrange
        string key = "123";
        _mockDatabase.Setup(db => db.KeyDeleteAsync(key, CommandFlags.None)).ReturnsAsync(true);

        // Act
        await _cacheProvider.RemoveAndForgetAsync<TestEntity>(key);

        // Assert
        _mockDatabase.Verify(db => db.KeyDeleteAsync("TestEntity:123", CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task ContainsAsync_ShouldReturnTrue_WhenKeyExists()
    {
        // Arrange
        string key = "123";
        _mockDatabase.Setup(db => db.KeyExistsAsync("TestEntity:123", CommandFlags.None)).ReturnsAsync(true);

        // Act
        var exists = await _cacheProvider.ContainsAsync<TestEntity>(key);

        // Assert
        Assert.True(exists);
        _mockDatabase.Verify(db => db.KeyExistsAsync("TestEntity:123", CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task ContainsAsync_ShouldReturnFalse_WhenKeyDoesNotExist()
    {
        // Arrange
        string key = "123";
        _mockDatabase.Setup(db => db.KeyExistsAsync(key, CommandFlags.None)).ReturnsAsync(false);

        // Act
        var exists = await _cacheProvider.ContainsAsync<TestEntity>(key);

        // Assert
        Assert.False(exists);
        _mockDatabase.Verify(db => db.KeyExistsAsync("TestEntity:123", CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task ClearAsync_ShouldDeleteAllKeysForGivenType()
    {
        // Arrange
        var document = Altruist.Database.Document.From(typeof(TestEntity));

        _mockServer.Setup(s => s.Keys(It.IsAny<int>(), It.IsAny<RedisValue>(), It.IsAny<int>(), It.IsAny<long>(), It.IsAny<int>(), It.IsAny<CommandFlags>()))
                   .Returns(["123", "456"]);

        _mockDatabase.Setup(db => db.KeyDeleteAsync(It.IsAny<RedisKey[]>(), CommandFlags.None)).ReturnsAsync(2);

        // Act
        await _cacheProvider.ClearAsync<TestEntity>();

        // Assert
        _mockDatabase.Verify(db => db.KeyDeleteAsync(It.IsAny<RedisKey[]>(), CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task SaveBatchAsync_ShouldSaveMultipleEntities()
    {
        // Arrange
        var entities = new Dictionary<string, TestEntity>
        {
            { "1", new TestEntity { GenId = "1", Name = "A" } },
            { "2", new TestEntity { GenId = "2", Name = "B" } }
        };

        _mockDatabase.Setup(db => db.CreateBatch(It.IsAny<object>())).Returns(_mockDatabaseBatch.Object);
        _mockDatabaseBatch.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), null, false, When.Always
        , CommandFlags.None)).ReturnsAsync(true);

        // Act
        await _cacheProvider.SaveBatchAsync(entities);

        // Assert
        _mockDatabase.Verify(db => db.CreateBatch(It.IsAny<object>()), Times.Once);
        _mockDatabaseBatch.Verify(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), null, false, When.Always
        , CommandFlags.None), Times.Exactly(2));
    }
}
