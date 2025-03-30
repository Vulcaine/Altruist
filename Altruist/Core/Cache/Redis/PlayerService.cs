using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Redis.OM.Searching;

namespace Altruist.Redis;

public class RedisPlayerService<TPlayerEntity> : IPlayerService<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    private IAltruistRedisConnectionProvider _provider;
    private IRedisCollection<TPlayerEntity> _entityRepo;

    private ILogger<RedisPlayerService<TPlayerEntity>> _logger;

    public RedisPlayerService(IAltruistRedisConnectionProvider provider, ILoggerFactory loggerFactory)
    {
        _provider = provider;
        _entityRepo = _provider.RedisCollection<TPlayerEntity>();
        _logger = loggerFactory.CreateLogger<RedisPlayerService<TPlayerEntity>>();
    }

    public async Task<TPlayerEntity> ConnectById(string roomId, string socketId, string name, float[]? position = null)
    {
        var player = new TPlayerEntity
        {
            Id = socketId,
            Name = name,
            Position = position ?? [0, 0]
        };

        await _provider.AddClientToRoom(socketId, roomId);
        await _entityRepo.InsertAsync(player);
        _logger.LogInformation($"Connected player {socketId} to instance {roomId}");

        return player;
    }

    public Task<TPlayerEntity?> FindEntityAsync(string playerId)
    {
        return _entityRepo.FindByIdAsync(playerId);
    }

    public async Task DisconnectAsync(string socketId)
    {
        await DeletePlayerAsync(socketId);
        _logger.LogInformation($"Player {socketId} disconnected and removed from Redis.");
    }

    public async Task UpdatePlayerAsync(TPlayerEntity player)
    {
        await _entityRepo.UpdateAsync(player);
        _logger.LogInformation($"Player {player.Id} updated in Redis.");
    }

    public Task<TPlayerEntity?> GetPlayer(string socketId)
    {
        return _provider.RedisCollection<TPlayerEntity>().FindByIdAsync(socketId);
    }

    public async Task DeletePlayerAsync(string playerId)
    {
        var player = await _entityRepo.FindByIdAsync(playerId);

        if (player != null)
        {
            await _entityRepo.DeleteAsync(player);

            _logger.LogInformation($"Player and associated spaceship with ID {player.Id} deleted.");
        }
    }

    public async Task<TPlayerEntity?> GetPlayerAsync(string playerId)
    {
        return await _entityRepo.FindByIdAsync(playerId);
    }

    public async Task Cleanup()
    {
        int cursor = 0;
        int batchSize = 100;

        do
        {
            var players = await _entityRepo.Skip(cursor).Take(batchSize).ToListAsync();

            if (!players.Any()) break;

            var playersToDelete = new List<string>();

            foreach (var player in players)
            {
                var conn = await _provider.GetConnection(player.Id);
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

        } while (true);

        _logger.LogInformation("Player cleanup completed.");
    }

}