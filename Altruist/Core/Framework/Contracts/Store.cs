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

namespace Altruist;

public interface ICleanUp
{
    Task Cleanup();
}

public interface IConnectionStore : ICleanUp
{
    Task<bool> IsConnectionExistsAsync(string connectionId);
    Task<bool> AddConnectionAsync(string connectionId, Connection socket, string? roomId = null);
    Task RemoveConnectionAsync(string connectionId);
    Task<Connection?> GetConnectionAsync(string connectionId);
    Task<Dictionary<string, Connection>> GetAllConnectionsAsync();
    Task<IEnumerable<string>> GetAllConnectionIdsAsync();

    Task<RoomPacket?> GetRoomAsync(string roomId);
    Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync();
    Task<Dictionary<string, Connection>> GetConnectionsInRoomAsync(string roomId);
    Task<RoomPacket> FindAvailableRoomAsync();
    Task<RoomPacket?> AddClientToRoomAsync(string connectionId, string roomId);
    Task<RoomPacket?> FindRoomForClientAsync(string clientId);
    Task<RoomPacket> CreateRoomAsync();
    Task SaveRoomAsync(RoomPacket room);
    Task DeleteRoomAsync(string roomId);
}


public abstract class AbstractConnectionStore : IConnectionStore
{
    protected readonly IMemoryCacheProvider _memoryCache;
    protected readonly ILogger _logger;

    public AbstractConnectionStore(IMemoryCacheProvider cache, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AbstractConnectionStore>();
        _memoryCache = cache;
    }

    public virtual async Task<bool> AddConnectionAsync(string connectionId, Connection socket, string? roomId = null)
    {
        await _memoryCache.SaveAsync(connectionId, socket);

        if (!string.IsNullOrEmpty(roomId))
        {
            var existingRoom = await _memoryCache.GetAsync<RoomPacket>(roomId);

            if (existingRoom == null)
            {
                return false;
            }

            existingRoom.ConnectionIds.Add(connectionId);
            await _memoryCache.SaveAsync(roomId, existingRoom);
            await _memoryCache.SaveAsync(connectionId, roomId);
            return true;
        }

        return true;
    }

    public virtual async Task RemoveConnectionAsync(string connectionId)
    {
        await _memoryCache.RemoveAndForgetAsync<Connection>(connectionId);

        var roomId = await _memoryCache.GetAsync<string>(connectionId);
        if (!string.IsNullOrEmpty(roomId))
        {
            var room = await _memoryCache.GetAsync<RoomPacket>(roomId);
            if (room != null)
            {
                room.ConnectionIds.Remove(connectionId);
                if (room.ConnectionIds.Count == 0)
                {
                    await _memoryCache.RemoveAndForgetAsync<RoomPacket>(roomId);
                }
                else
                {
                    await _memoryCache.SaveAsync(roomId, room);
                }
            }
        }
    }

    public virtual async Task<RoomPacket?> FindRoomForClientAsync(string clientId)
    {
        var roomId = await _memoryCache.GetAsync<string>(clientId);
        if (!string.IsNullOrEmpty(roomId))
        {
            var room = await _memoryCache.GetAsync<RoomPacket>(roomId);
            if (room != null)
            {
                return room;
            }
        }

        return null;
    }

    public virtual async Task<Connection?> GetConnectionAsync(string connectionId)
    {
        return await _memoryCache.GetAsync<Connection>(connectionId);
    }

    public virtual async Task<Dictionary<string, Connection>> GetAllConnectionsAsync()
    {
        var connections = new Dictionary<string, Connection>();

        var cursor = await _memoryCache.GetAllAsync<Connection>();
        foreach (var connection in cursor)
        {
            connections[connection.ConnectionId] = connection;
        }

        return connections;
    }

    public virtual async Task<IEnumerable<string>> GetAllConnectionIdsAsync()
    {
        var connectionIds = new List<string>();

        var cursor = await _memoryCache.GetAllAsync<Connection>();
        foreach (var connection in cursor)
        {
            connectionIds.Add(connection.ConnectionId);
        }

        return connectionIds;
    }

    public virtual async Task<Dictionary<string, Connection>> GetConnectionsInRoomAsync(string roomId)
    {
        var room = await _memoryCache.GetAsync<RoomPacket>(roomId);
        if (room != null)
        {
            var connectionsInRoom = new Dictionary<string, Connection>();
            foreach (var connectionId in room.ConnectionIds)
            {
                var connection = await _memoryCache.GetAsync<Connection>(connectionId);
                if (connection != null)
                {
                    connectionsInRoom[connectionId] = connection;
                }
            }

            return connectionsInRoom;
        }

        return new Dictionary<string, Connection>();
    }

    public virtual async Task<RoomPacket?> GetRoomAsync(string roomId)
    {
        var room = await _memoryCache.GetAsync<RoomPacket>(roomId);
        if (room == null)
        {
            return new RoomPacket(roomId);
        }
        return room;
    }

    public virtual async Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync()
    {
        var rooms = new Dictionary<string, RoomPacket>();

        var cursor = await _memoryCache.GetAllAsync<RoomPacket>();
        foreach (var room in cursor)
        {
            rooms[room.Id] = room;
        }

        return rooms;
    }

    public virtual async Task<RoomPacket> FindAvailableRoomAsync()
    {
        var cursor = await _memoryCache.GetAllAsync<RoomPacket>();
        foreach (var room in cursor)
        {
            if (room.ConnectionIds.Count < room.MaxCapactiy)
            {
                return room;
            }
        }

        return await CreateRoomAsync();
    }

    public virtual async Task DeleteRoomAsync(string roomId)
    {
        await _memoryCache.RemoveAndForgetAsync<RoomPacket>(roomId);
    }

    public virtual async Task<RoomPacket> CreateRoomAsync()
    {
        var roomId = $"{Guid.NewGuid()}";
        var room = new RoomPacket(roomId);

        await _memoryCache.SaveAsync(roomId, room);

        return room;
    }

    public virtual async Task<RoomPacket?> AddClientToRoomAsync(string connectionId, string roomId)
    {
        var room = await GetRoomAsync(roomId);
        if (room == null)
        {
            return null;
        }
        room = room.AddConnection(connectionId);
        await SaveRoomAsync(room);
        await _memoryCache.SaveAsync(connectionId, roomId);
        return room;
    }

    public virtual async Task SaveRoomAsync(RoomPacket room)
    {
        await _memoryCache.SaveAsync(room.Id, room);
    }

    public virtual async Task Cleanup()
    {
        var removed = new List<string>();
        var cursor = await _memoryCache.GetAllAsync<Connection>();
        foreach (var connection in cursor)
        {
            if (!connection.IsConnected)
            {
                await RemoveConnectionAsync(connection.ConnectionId);
                removed.Add(connection.ConnectionId);
            }
        }

        if (removed.Count > 0)
        {
            _logger.LogInformation("Inactive connections have been removed from memory.");
        }
    }

    public virtual Task<bool> IsConnectionExistsAsync(string connectionId)
    {
        return _memoryCache.ContainsAsync<Connection>(connectionId);
    }
}
