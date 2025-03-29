using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Redis.OM.Searching;

namespace Altruist.Redis;

public class RedisPlayerService<TPlayerEntity> : IPlayerService<TPlayerEntity> where TPlayerEntity : PlayerEntity
{
    private IAltruistRedisConnectionProvider _provider;
    private IRedisCollection<Player> _playerRepo;
    private IRedisCollection<TPlayerEntity> _entityRepo;

    private ILogger<RedisPlayerService<TPlayerEntity>> _logger;

    public RedisPlayerService(IAltruistRedisConnectionProvider provider, ILoggerFactory loggerFactory)
    {
        _provider = provider;
        _playerRepo = _provider.RedisCollection<Player>();
        _entityRepo = _provider.RedisCollection<TPlayerEntity>();
        _logger = loggerFactory.CreateLogger<RedisPlayerService<TPlayerEntity>>();
    }

    public async Task<Player> ConnectById(string roomId, string socketId, string name)
    {
        var player = new Player
        {
            Id = socketId,
            Name = name
        };

        await _provider.AddClientToRoom(socketId, roomId);
        await _playerRepo.InsertAsync(player);
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

    public async Task UpdatePlayerAsync(Player player)
    {
        await _playerRepo.UpdateAsync(player);
        _logger.LogInformation($"Player {player.Id} updated in Redis.");
    }

    public Task<Player?> GetPlayer(string socketId)
    {
        return _provider.RedisCollection<Player>().FindByIdAsync(socketId);
    }

    public async Task DeletePlayerAsync(string playerId)
    {
        var player = await _playerRepo.FindByIdAsync(playerId);

        if (player != null)
        {
            await _playerRepo.DeleteAsync(player);

            _logger.LogInformation($"Player and associated spaceship with ID {player.Id} deleted.");
        }
    }

    public async Task<Player?> GetPlayerAsync(string playerId)
    {
        return await _playerRepo.FindByIdAsync(playerId);
    }

    public async Task Cleanup()
    {
        int cursor = 0;
        int batchSize = 100;

        do
        {
            var players = await _playerRepo.Skip(cursor).Take(batchSize).ToListAsync();

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