using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Altruist.InMemory;

public class InMemoryPlayerService<TPlayerEntity> : IPlayerService<TPlayerEntity> where TPlayerEntity : PlayerEntity
{
    private readonly IConnectionStore _store;
    private readonly ConcurrentDictionary<string, Player> _players = new();
    private readonly ConcurrentDictionary<string, TPlayerEntity> _entities = new();
    private ILogger<InMemoryPlayerService<TPlayerEntity>> _logger;

    public InMemoryPlayerService(IConnectionStore store, ILoggerFactory loggerFactory)
    {
        _store = store;
        _logger = loggerFactory.CreateLogger<InMemoryPlayerService<TPlayerEntity>>();
    }

    public async Task<Player> ConnectById(string roomId, string socketId, string name)
    {
        var player = new Player
        {
            Id = socketId,
            Name = name
        };

        _players[socketId] = player;
        await _store.AddClientToRoom(socketId, roomId);

        _logger.LogInformation($"Connected player {socketId} to instance {roomId}");
        return player;
    }

    public Task<TPlayerEntity?> FindEntityAsync(string playerId)
    {
        _entities.TryGetValue(playerId, out var entity);
        return Task.FromResult(entity);
    }

    public Task DisconnectAsync(string socketId)
    {
        return DeletePlayerAsync(socketId);
    }

    public Task UpdatePlayerAsync(Player player)
    {
        _players[player.Id!] = player;

        _logger.LogInformation($"Player {player.Id} updated in memory.");
        return Task.CompletedTask;
    }

    public Task<Player?> GetPlayer(string socketId)
    {
        _players.TryGetValue(socketId, out var player);
        return Task.FromResult(player);
    }

    public Task DeletePlayerAsync(string playerId)
    {
        if (_players.TryGetValue(playerId, out var player))
        {
            _players.Remove(playerId, out _);
            if (player.Id != null && _entities.ContainsKey(player.Id))
            {
                _entities.Remove(player.Id, out _);
            }
            _logger.LogInformation($"Player and associated entity with ID {playerId} deleted from memory.");
        }
        else
        {
            _logger.LogWarning($"Player with ID {playerId} not found.");
        }
        return Task.CompletedTask;
    }

    public Task<Player?> GetPlayerAsync(string playerId)
    {
        return GetPlayer(playerId);
    }

    public async Task Cleanup()
    {
        foreach (var player in _players.Values)
        {
            if (player.Id != null && _entities.ContainsKey(player.Id))
            {
                var conn = await _store.GetConnection(player.Id);
                if (conn == null || !conn.IsConnected)
                {
                    await DeletePlayerAsync(player.Id);
                }
            }
        }
    }
}
