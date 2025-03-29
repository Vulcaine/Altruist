namespace Altruist.InMemory;

public class InMemoryCacheTests
{
    private readonly InMemoryCache _cache;

    public InMemoryCacheTests()
    {
        _cache = new InMemoryCache();
    }

    [Fact]
    public async Task SaveAsync_ShouldSaveEntity()
    {
        // Arrange
        var key = "player1";
        var entity = new Player { Id = 1, Name = "John" };

        // Act
        await _cache.SaveAsync(key, entity);

        // Assert
        var savedEntity = await _cache.GetAsync<Player>(key);
        Assert.NotNull(savedEntity);
        Assert.Equal("John", savedEntity.Name);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnEntity_WhenExists()
    {
        // Arrange
        var key = "player1";
        var entity = new Player { Id = 1, Name = "John" };
        await _cache.SaveAsync(key, entity);

        // Act
        var retrievedEntity = await _cache.GetAsync<Player>(key);

        // Assert
        Assert.NotNull(retrievedEntity);
        Assert.Equal("John", retrievedEntity.Name);
    }

    [Fact]
    public async Task GetAsync_ShouldReturnNull_WhenEntityDoesNotExist()
    {
        // Arrange
        var key = "nonexistent_key";

        // Act
        var retrievedEntity = await _cache.GetAsync<Player>(key);

        // Assert
        Assert.Null(retrievedEntity);
    }

    [Fact]
    public async Task RemoveAsync_ShouldRemoveEntity_WhenExists()
    {
        // Arrange
        var key = "player1";
        var entity = new Player { Id = 1, Name = "John" };
        await _cache.SaveAsync(key, entity);

        // Act
        var removed = await _cache.RemoveAsync<Player>(key);

        // Assert
        Assert.NotNull(removed);
        var retrievedEntity = await _cache.GetAsync<Player>(key);
        Assert.Null(retrievedEntity);
    }

    [Fact]
    public async Task RemoveAsync_ShouldReturnFalse_WhenEntityDoesNotExist()
    {
        // Arrange
        var key = "nonexistent_key";

        // Act
        var removed = await _cache.RemoveAsync<Player>(key);

        // Assert
        Assert.Null(removed);
    }

    [Fact]
    public async Task SaveBatchAsync_ShouldSaveMultipleEntities()
    {
        // Arrange
        var entities = new Dictionary<string, Player>
        {
            { "player1", new Player { Id = 1, Name = "John" } },
            { "player2", new Player { Id = 2, Name = "Jane" } }
        };

        // Act
        await _cache.SaveBatchAsync(entities);

        // Assert
        var retrievedPlayer1 = await _cache.GetAsync<Player>("player1");
        var retrievedPlayer2 = await _cache.GetAsync<Player>("player2");

        Assert.NotNull(retrievedPlayer1);
        Assert.Equal("John", retrievedPlayer1.Name);
        Assert.NotNull(retrievedPlayer2);
        Assert.Equal("Jane", retrievedPlayer2.Name);
    }

    [Fact]
    public async Task ClearAsync_ShouldClearAllCache()
    {
        // Arrange
        await _cache.SaveAsync("player1", new Player { Id = 1, Name = "John" });
        await _cache.SaveAsync("player2", new Player { Id = 2, Name = "Jane" });

        // Act
        await _cache.ClearAsync<Player>();

        // Assert
        var retrievedPlayer1 = await _cache.GetAsync<Player>("player1");
        var retrievedPlayer2 = await _cache.GetAsync<Player>("player2");

        Assert.Null(retrievedPlayer1);
        Assert.Null(retrievedPlayer2);
    }

    // Example of a player class for testing
    public class Player
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }
}
