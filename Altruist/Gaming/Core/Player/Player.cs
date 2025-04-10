using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

public class AltruistPlayerService<TPlayerEntity> : IPlayerService<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    private readonly IConnectionStore _store;
    private readonly ICacheProvider _cacheProvider;

    private ILogger<AltruistPlayerService<TPlayerEntity>> _logger;

    public AltruistPlayerService(IConnectionStore store, ICacheProvider cacheProvider, ILoggerFactory loggerFactory)
    {
        _store = store;
        _logger = loggerFactory.CreateLogger<AltruistPlayerService<TPlayerEntity>>();
        _cacheProvider = cacheProvider;
    }

    public async Task<TPlayerEntity?> ConnectById(string roomId, string socketId, string name, float[]? position = null)
    {
        var player = new TPlayerEntity
        {
            Id = socketId,
            Name = name,
            Position = position ?? [0, 0]
        };

        var room = await _store.AddClientToRoomAsync(socketId, roomId);
        if (room == null)
        {
            _logger.LogError($"Failed to connect player {socketId} to instance {roomId}. No such room");
            return null;
        }
        else
        {
            await _cacheProvider.SaveAsync(socketId, player);
            _logger.LogInformation($"Connected player {socketId} to instance {roomId}");
        }


        return player;
    }


    public Task<TPlayerEntity?> FindEntityAsync(string playerId)
    {
        return _cacheProvider.GetAsync<TPlayerEntity>(playerId);
    }

    public async Task DisconnectAsync(string socketId)
    {
        await DeletePlayerAsync(socketId);
        _logger.LogInformation($"Player {socketId} disconnected and removed from Redis.");
    }

    public async Task UpdatePlayerAsync(TPlayerEntity player)
    {
        await _cacheProvider.SaveAsync(player.Id, player);
        _logger.LogInformation($"Player {player.Id} updated in Redis.");
    }

    public Task<TPlayerEntity?> GetPlayer(string socketId)
    {
        return _cacheProvider.GetAsync<TPlayerEntity>(socketId);
    }

    public async Task DeletePlayerAsync(string playerId)
    {
        var player = await FindEntityAsync(playerId);

        if (player != null)
        {
            await _cacheProvider.RemoveAndForgetAsync<TPlayerEntity>(playerId);

            _logger.LogInformation($"Player and associated spaceship with ID {player.Id} deleted.");
        }
    }

    public async Task<TPlayerEntity?> GetPlayerAsync(string playerId)
    {
        return await FindEntityAsync(playerId);
    }


    public async Task Cleanup()
    {
        int cursor = 0;
        int batchSize = 100;

        var players = await _cacheProvider.GetAllAsync<TPlayerEntity>();
        var playersToDelete = new List<string>();

        foreach (var player in players)
        {
            var conn = await _store.GetConnectionAsync(player.ConnectionId);
            if (conn == null || !conn.IsConnected)
            {
                playersToDelete.Add(player.Id);
            }
        }

        if (playersToDelete.Any())
        {
            await Task.WhenAll(playersToDelete.Select(id => DeletePlayerAsync(id)));
        }

        cursor += batchSize;

        _logger.LogInformation("Player cleanup completed.");
    }
}
