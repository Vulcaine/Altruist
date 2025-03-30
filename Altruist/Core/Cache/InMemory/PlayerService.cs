using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Altruist.InMemory;

public class InMemoryPlayerService<TPlayerEntity> : IPlayerService<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    private readonly IConnectionStore _store;
    // private readonly ConcurrentDictionary<string, Player> _players = new();
    private readonly ConcurrentDictionary<string, TPlayerEntity> _entities = new();
    private ILogger<InMemoryPlayerService<TPlayerEntity>> _logger;

    public InMemoryPlayerService(IConnectionStore store, ILoggerFactory loggerFactory)
    {
        _store = store;
        _logger = loggerFactory.CreateLogger<InMemoryPlayerService<TPlayerEntity>>();
    }

    public async Task<TPlayerEntity?> ConnectById(string roomId, string socketId, string name, float[]? position = null)
    {
        var player = new TPlayerEntity
        {
            Id = socketId,
            Name = name,
            Position = position ?? [0, 0]
        };

        _entities[socketId] = player;
        var room = await _store.AddClientToRoom(socketId, roomId);

        if (room == null)
        {
            _logger.LogError($"Failed to connect player {socketId} to instance {roomId}. No such room");
            return null;
        }
        else
        {
            _logger.LogInformation($"Connected player {socketId} to instance {roomId}");
        }

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

    public Task UpdatePlayerAsync(TPlayerEntity player)
    {
        _entities[player.Id!] = player;

        _logger.LogInformation($"Player {player.Id} updated in memory.");
        return Task.CompletedTask;
    }

    public Task<TPlayerEntity?> GetPlayer(string socketId)
    {
        _entities.TryGetValue(socketId, out var player);
        return Task.FromResult(player);
    }

    public Task DeletePlayerAsync(string playerId)
    {
        if (_entities.TryGetValue(playerId, out var player))
        {
            _entities.Remove(playerId, out _);
            _logger.LogInformation($"Player and associated entity with ID {playerId} deleted from memory.");
        }
        else
        {
            _logger.LogWarning($"Player with ID {playerId} not found.");
        }
        return Task.CompletedTask;
    }

    public Task<TPlayerEntity?> GetPlayerAsync(string playerId)
    {
        return GetPlayer(playerId);
    }

    public async Task Cleanup()
    {
        foreach (var player in _entities.Values)
        {
            var conn = await _store.GetConnection(player.Id);
            if (conn == null || !conn.IsConnected)
            {
                await DeletePlayerAsync(player.Id);
            }
        }
    }
}
