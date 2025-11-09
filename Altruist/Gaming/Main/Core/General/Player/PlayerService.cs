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

using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

[Service(typeof(IPlayerService))]
public class AltruistPlayerService : IPlayerService
{
    private readonly IConnectionStore _store;
    private readonly ICacheProvider _cacheProvider;

    private readonly IGameWorldCoordinator _worldCoordinator;

    private ILogger<AltruistPlayerService> _logger;

    public AltruistPlayerService(IConnectionStore store, ICacheProvider cacheProvider, IGameWorldCoordinator worldCoordinator, ILoggerFactory loggerFactory)
    {
        _store = store;
        _logger = loggerFactory.CreateLogger<AltruistPlayerService>();
        _cacheProvider = cacheProvider;
        _worldCoordinator = worldCoordinator;
    }

    public async Task<PlayerEntity?> ConnectById(string roomId, string socketId, string name, int worldIndex, float[]? position = null)
    {
        var player = new PlayerEntity
        {
            StorageId = socketId,
            ConnectionId = socketId,
            Name = name,
            Position = position ?? [0, 0],
            WorldIndex = worldIndex
        };

        var world = _worldCoordinator.GetWorld(worldIndex);

        if (world == null)
        {
            _logger.LogError($"Failed to connect player {socketId} to instance {roomId} and world {worldIndex}. No such world.");
            return null;
        }

        // player.CalculatePhysxBody(world.PhysxWorld.World);
        var room = await _store.AddClientToRoomAsync(socketId, roomId);
        if (room == null)
        {
            _logger.LogError($"Failed to connect player {socketId} to instance {roomId}. No such room");
            return null;
        }
        else
        {
            await _cacheProvider.SaveAsync(socketId, player);
            _logger.LogInformation($"Connected player {socketId} to room: {room}");
        }


        return player;
    }


    public Task<PlayerEntity?> FindEntityAsync(string playerId)
    {
        return _cacheProvider.GetAsync<PlayerEntity>(playerId);
    }

    public async Task DisconnectAsync(string socketId)
    {
        await DeletePlayerAsync(socketId);
        _logger.LogInformation($"Player {socketId} disconnected and removed from Redis.");
    }

    public async Task UpdatePlayerAsync(PlayerEntity player)
    {
        await _cacheProvider.SaveAsync(player.StorageId, player);
        _logger.LogInformation($"Player {player.StorageId} updated in Redis.");
    }

    public Task<PlayerEntity?> GetPlayer(string socketId)
    {
        return _cacheProvider.GetAsync<PlayerEntity>(socketId);
    }

    public async Task DeletePlayerAsync(string playerId)
    {
        var player = await FindEntityAsync(playerId);

        if (player != null)
        {
            await _cacheProvider.RemoveAndForgetAsync<PlayerEntity>(playerId);

            _logger.LogInformation($"Player and associated spaceship with ID {player.StorageId} deleted.");
        }
    }

    public async Task<PlayerEntity?> GetPlayerAsync(string playerId)
    {
        return await FindEntityAsync(playerId);
    }


    public async Task Cleanup()
    {
        int cursor = 0;
        int batchSize = 100;

        var players = await _cacheProvider.GetAllAsync<PlayerEntity>();
        var playersToDelete = new List<string>();

        foreach (var player in players)
        {
            var conn = await _store.GetConnectionAsync(player.ConnectionId);
            if (conn == null || !conn.IsConnected)
            {
                playersToDelete.Add(player.StorageId);
                player.DetachBody();
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
