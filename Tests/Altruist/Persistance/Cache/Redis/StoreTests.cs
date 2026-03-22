/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Net;
using System.Text.Json;
using Altruist;
using Altruist.InMemory;
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
        _mockMultiplexer.Setup(m => m.IsConnected).Returns(true);
        _mockDatabase.Setup(m => m.Multiplexer).Returns(_mockMultiplexer.Object);

        _configuration.AddDocument<TestEntity>();

        var mockFactory = new Mock<RedisConnectionFactory>();
        mockFactory.Setup(f => f.Multiplexer).Returns(_mockMultiplexer.Object);

        _cacheProvider = new RedisCacheProvider(mockFactory.Object);
    }

    [Altruist.UORM.Vault("TestEntity")]
    [Altruist.UORM.VaultPrimaryKey("StorageId")]
    private class TestEntity : IStoredModel
    {
        public string StorageId { get; set; } = Guid.NewGuid().ToString();
        public string Name { get; set; } = "Test";
        public string Type { get; set; } = "TestEntity";
    }

    [Fact]
    public async Task SaveAsync_ShouldSaveEntityToRedis()
    {
        var testEntity = new TestEntity { StorageId = "123", Name = "Test" };
        string key = "123";

        _mockDatabase.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), null, false, When.Always,
            CommandFlags.None))
            .ReturnsAsync(true);

        await _cacheProvider.SaveRemoteAsync(key, testEntity);

        _mockDatabase.Verify(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), null, false, When.Always,
            CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnEntity_WhenExistsInRedis()
    {
        var testEntity = new TestEntity { StorageId = "123", Name = "Test" };
        string key = "123";
        string json = JsonSerializer.Serialize(testEntity);

        _mockDatabase.Setup(db => db.StringGetAsync("TestEntity:123", CommandFlags.None)).ReturnsAsync(json);

        var result = await _cacheProvider.GetRemoteAsync<TestEntity>(key);

        Assert.NotNull(result);
        Assert.Equal(testEntity.Name, result.Name);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenKeyDoesNotExist()
    {
        string key = "nonexistent";

        var result = await _cacheProvider.GetAsync<TestEntity>(key);

        Assert.Null(result);
    }

    [Fact]
    public async Task RemoveAsync_ShouldDeleteEntityFromRedis()
    {
        string key = "123";
        _mockDatabase.Setup(db => db.KeyDeleteAsync("TestEntity:123", CommandFlags.None)).ReturnsAsync(true);

        await _cacheProvider.RemoveAndForgetAsync<TestEntity>(key);

        _mockDatabase.Verify(db => db.KeyDeleteAsync("TestEntity:123", CommandFlags.None), Times.Once);
    }

    [Fact]
    public async Task ContainsAsync_ShouldReturnTrue_WhenKeyExists()
    {
        string key = "123";
        _mockDatabase.Setup(db => db.KeyExistsAsync("TestEntity:123", CommandFlags.None)).ReturnsAsync(true);

        var exists = await _cacheProvider.ContainsAsync<TestEntity>(key);

        Assert.True(exists);
    }

    [Fact]
    public async Task ContainsAsync_ShouldReturnFalse_WhenKeyDoesNotExist()
    {
        string key = "123";
        _mockDatabase.Setup(db => db.KeyExistsAsync("TestEntity:123", CommandFlags.None)).ReturnsAsync(false);

        var exists = await _cacheProvider.ContainsAsync<TestEntity>(key);

        Assert.False(exists);
    }

    [Fact]
    public async Task SaveBatchRemoteAsync_ShouldSaveMultipleEntities()
    {
        var entities = new Dictionary<string, TestEntity>
        {
            { "1", new TestEntity { StorageId = "1", Name = "A" } },
            { "2", new TestEntity { StorageId = "2", Name = "B" } }
        };

        _mockDatabase.Setup(db => db.CreateBatch(It.IsAny<object>())).Returns(_mockDatabaseBatch.Object);
        _mockDatabaseBatch.Setup(db => db.StringSetAsync(It.IsAny<RedisKey>(), It.IsAny<RedisValue>(), null, false, When.Always,
            CommandFlags.None)).ReturnsAsync(true);

        await _cacheProvider.SaveBatchRemoteAsync(entities);

        _mockDatabase.Verify(db => db.CreateBatch(It.IsAny<object>()), Times.Once);
    }

    [Fact]
    public void GetSnapshot_ShouldReturnInMemorySnapshot()
    {
        var snapshot = _cacheProvider.GetSnapshot();
        Assert.NotNull(snapshot);
    }

    [Fact]
    public void IsConnected_ShouldReturnTrue_WhenMultiplexerConnected()
    {
        Assert.True(_cacheProvider.IsConnected);
    }
}
