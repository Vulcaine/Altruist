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

public static class StoreConstants
{
    public static string WaitingRoomId = "waiting_room";
}

public interface ICleanUp
{
    Task Cleanup();
}

public interface IConnectionStore : ICleanUp
{
    Task<bool> IsConnectionExistsAsync(string connectionId);
    Task<bool> AddConnectionAsync(string connectionId, AltruistConnection socket, string? roomId = null);
    Task RemoveConnectionAsync(string connectionId);
    Task<AltruistConnection?> GetConnectionAsync(string connectionId);
    Task<ICursor<AltruistConnection>> GetAllConnectionsAsync();
    Task<Dictionary<string, AltruistConnection>> GetAllConnectionsDictAsync();
    Task<IEnumerable<string>> GetAllConnectionIdsAsync();
    Task<RoomPacket?> GetRoomAsync(string roomId);
    Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync();
    Task<Dictionary<string, AltruistConnection>> GetConnectionsInRoomAsync(string roomId);
    Task<RoomPacket?> FindAvailableRoomAsync();
    Task<RoomPacket?> JoinRoomAsync(string connectionId, string roomId);
    Task<RoomPacket?> FindRoomForClientAsync(string clientId);
    Task<RoomPacket> CreateRoomAsync(string? roomId);
    Task SaveRoomAsync(RoomPacket room);
    Task DeleteRoomAsync(string roomId);
}


public abstract class AbstractConnectionStore : IConnectionStore
{
    protected readonly IMemoryCacheProvider _memoryCache;
    protected readonly ILogger _logger;

    /// <summary>
    /// Serializes room membership changes. RoomPacket.ConnectionIds is a plain HashSet shared by
    /// every connection (all sockets start in the waiting room), and connects/disconnects run on
    /// many threads at once; unsynchronized Add/Remove corrupts the set under a connect storm.
    /// </summary>
    private readonly SemaphoreSlim _roomLock = new(1, 1);

    public AbstractConnectionStore(IMemoryCacheProvider cache, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AbstractConnectionStore>();
        _memoryCache = cache;
    }

    public virtual async Task<bool> AddConnectionAsync(string connectionId, AltruistConnection socket, string? roomId = null)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
        {
            return false;
        }

        socket.SetId(connectionId);
        await _memoryCache.SaveAsync(connectionId, socket);

        if (!string.IsNullOrEmpty(roomId))
        {
            return await JoinRoomAsync(connectionId, roomId) != null;
        }

        return true;
    }

    public virtual async Task RemoveConnectionAsync(string connectionId)
    {
        var roomId = await _memoryCache.GetAsync<string>(connectionId);
        await _memoryCache.RemoveAndForgetAsync<AltruistConnection>(connectionId);
        await _memoryCache.RemoveAndForgetAsync<string>(connectionId);

        if (!string.IsNullOrEmpty(roomId))
        {
            await RemoveConnectionFromRoomAsync(connectionId, roomId);
        }
        else
        {
            await RemoveConnectionFromAllRoomsAsync(connectionId);
        }
    }

    private async Task RemoveConnectionFromRoomAsync(string connectionId, string roomId)
    {
        await _roomLock.WaitAsync();
        try
        {
            var room = await _memoryCache.GetAsync<RoomPacket>(roomId);
            if (room == null)
            {
                return;
            }

            if (!room.ConnectionIds.Remove(connectionId))
            {
                return;
            }

            if (room.ConnectionIds.Count == 0 && !string.Equals(room.Id, StoreConstants.WaitingRoomId, StringComparison.Ordinal))
            {
                await _memoryCache.RemoveAndForgetAsync<RoomPacket>(roomId);
            }
            else
            {
                await _memoryCache.SaveAsync(roomId, room);
            }
        }
        finally
        {
            _roomLock.Release();
        }
    }

    private async Task RemoveConnectionFromAllRoomsAsync(string connectionId)
    {
        await _roomLock.WaitAsync();
        try
        {
            var cursor = await _memoryCache.GetAllAsync<RoomPacket>();
            foreach (var room in cursor)
            {
                if (!room.ConnectionIds.Remove(connectionId))
                {
                    continue;
                }

                if (room.ConnectionIds.Count == 0 && !string.Equals(room.Id, StoreConstants.WaitingRoomId, StringComparison.Ordinal))
                {
                    await _memoryCache.RemoveAndForgetAsync<RoomPacket>(room.Id);
                }
                else
                {
                    await _memoryCache.SaveAsync(room.Id, room);
                }
            }
        }
        finally
        {
            _roomLock.Release();
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

    public virtual async Task<AltruistConnection?> GetConnectionAsync(string connectionId)
    {
        return await _memoryCache.GetAsync<AltruistConnection>(connectionId);
    }

    public virtual async Task<Dictionary<string, AltruistConnection>> GetAllConnectionsDictAsync()
    {
        var connections = new Dictionary<string, AltruistConnection>();

        var cursor = await _memoryCache.GetAllAsync<AltruistConnection>();
        foreach (var connection in cursor)
        {
            connections[connection.ConnectionId] = connection;
        }

        return connections;
    }

    public virtual async Task<ICursor<AltruistConnection>> GetAllConnectionsAsync()
    {
        return await _memoryCache.GetAllAsync<AltruistConnection>();
    }

    public virtual async Task<IEnumerable<string>> GetAllConnectionIdsAsync()
    {
        var connectionIds = new List<string>();

        var cursor = await _memoryCache.GetAllAsync<AltruistConnection>();
        foreach (var connection in cursor)
        {
            connectionIds.Add(connection.ConnectionId);
        }

        return connectionIds;
    }

    public virtual async Task<Dictionary<string, AltruistConnection>> GetConnectionsInRoomAsync(string roomId)
    {
        var room = await _memoryCache.GetAsync<RoomPacket>(roomId);
        if (room != null)
        {
            var connectionsInRoom = new Dictionary<string, AltruistConnection>();
            string[] ids;
            await _roomLock.WaitAsync();
            try
            {
                ids = room.ConnectionIds.ToArray();
            }
            finally
            {
                _roomLock.Release();
            }
            foreach (var connectionId in ids)
            {
                var connection = await _memoryCache.GetAsync<AltruistConnection>(connectionId);
                if (connection != null)
                {
                    connectionsInRoom[connectionId] = connection;
                }
            }

            return connectionsInRoom;
        }

        return new Dictionary<string, AltruistConnection>();
    }

    public virtual async Task<RoomPacket?> GetRoomAsync(string roomId)
    {
        return await _memoryCache.GetAsync<RoomPacket>(roomId);

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

    public virtual async Task<RoomPacket?> FindAvailableRoomAsync()
    {
        var cursor = await _memoryCache.GetAllAsync<RoomPacket>();
        foreach (var room in cursor)
        {
            if (room.ConnectionIds.Count < room.MaxCapactiy)
            {
                return room;
            }
        }

        return null;
    }

    public virtual async Task DeleteRoomAsync(string roomId)
    {
        await _memoryCache.RemoveAndForgetAsync<RoomPacket>(roomId);
    }

    public virtual async Task<RoomPacket> CreateRoomAsync(string? roomId = null)
    {

        var roomName = roomId ?? $"{Guid.NewGuid()}";
        var existingRoom = await GetRoomAsync(roomName);

        if (existingRoom != null)
        {
            return existingRoom;
        }

        var room = new RoomPacket(roomName);
        await _memoryCache.SaveAsync(roomName, room);
        return room;
    }

    public virtual async Task<RoomPacket?> JoinRoomAsync(string connectionId, string roomId)
    {
        await _roomLock.WaitAsync();
        try
        {
            var existingRoomId = await _memoryCache.GetAsync<string>(connectionId);
            if (!string.IsNullOrEmpty(existingRoomId))
            {
                if (string.Equals(existingRoomId, roomId, StringComparison.Ordinal))
                {
                    return await GetRoomAsync(roomId);
                }

                var existingRoom = await _memoryCache.GetAsync<RoomPacket>(existingRoomId);
                if (existingRoom != null)
                {
                    if (existingRoom.ConnectionIds.Remove(connectionId))
                    {
                        // Like every other removal path: the waiting room is never deleted (new
                        // connections could no longer join it once everyone had moved on).
                        if (existingRoom.ConnectionIds.Count == 0
                            && !string.Equals(existingRoomId, StoreConstants.WaitingRoomId, StringComparison.Ordinal))
                        {
                            await _memoryCache.RemoveAndForgetAsync<RoomPacket>(existingRoomId);
                        }
                        else
                        {
                            await _memoryCache.SaveAsync(existingRoomId, existingRoom);
                        }
                    }
                }
            }

            var room = await GetRoomAsync(roomId);
            if (room == null)
            {
                return null;
            }

            room = room.AddConnection(connectionId);
            await SaveRoomAsync(room);
            await _memoryCache.SaveAsync(connectionId, room.Id);

            return room;
        }
        finally
        {
            _roomLock.Release();
        }
    }

    public virtual async Task SaveRoomAsync(RoomPacket room)
    {
        await _memoryCache.SaveAsync(room.Id, room);
    }

    public virtual async Task Cleanup()
    {
        var removed = new List<string>();
        var cursor = await _memoryCache.GetAllAsync<AltruistConnection>();
        foreach (var connection in cursor)
        {
            if (!connection.IsConnected)
            {
                await RemoveConnectionAsync(connection.ConnectionId);
                removed.Add(connection.ConnectionId);
            }
        }

        await PruneRoomsAsync();

        if (removed.Count > 0)
        {
            _logger.LogInformation("Inactive connections have been removed from memory.");
        }

        await CreateRoomAsync(StoreConstants.WaitingRoomId);
    }

    private async Task PruneRoomsAsync()
    {
        await _roomLock.WaitAsync();
        try
        {
            var cursor = await _memoryCache.GetAllAsync<RoomPacket>();
            foreach (var room in cursor)
            {
                var changed = false;
                foreach (var connectionId in room.ConnectionIds.ToArray())
                {
                    if (await _memoryCache.ContainsAsync<AltruistConnection>(connectionId))
                    {
                        continue;
                    }

                    room.ConnectionIds.Remove(connectionId);
                    await _memoryCache.RemoveAndForgetAsync<string>(connectionId);
                    changed = true;
                }

                if (!changed)
                {
                    continue;
                }

                if (room.ConnectionIds.Count == 0 && !string.Equals(room.Id, StoreConstants.WaitingRoomId, StringComparison.Ordinal))
                {
                    await _memoryCache.RemoveAndForgetAsync<RoomPacket>(room.Id);
                }
                else
                {
                    await _memoryCache.SaveAsync(room.Id, room);
                }
            }
        }
        finally
        {
            _roomLock.Release();
        }
    }

    public virtual Task<bool> IsConnectionExistsAsync(string connectionId)
    {
        return _memoryCache.ContainsAsync<AltruistConnection>(connectionId);
    }
}
