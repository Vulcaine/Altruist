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

/// <summary>Well-known ids used by the connection store.</summary>
public static class StoreConstants
{
    /// <summary>
    /// Id of the waiting room: every new connection joins it on connect, and it is never deleted when empty
    /// (the store's cleanup recreates it). Move clients out of it with <see cref="IConnectionStore.JoinRoomAsync"/>.
    /// </summary>
    public static string WaitingRoomId = "waiting_room";
}

/// <summary>A component that can prune stale state on demand.</summary>
public interface ICleanUp
{
    /// <summary>Removes stale entries (for the connection store: disconnected connections and dangling room members).</summary>
    Task Cleanup();
}

/// <summary>
/// Storage of client connections and rooms (groups of connections). A connection is in at most one room at a time;
/// every connection starts in <see cref="StoreConstants.WaitingRoomId"/>. All senders
/// (<see cref="ClientSender"/>, <see cref="RoomSender"/>, <see cref="BroadcastSender"/>) resolve connections through it.
/// </summary>
/// <remarks>
/// The default implementation is <c>InMemoryConnectionStore</c> (singleton, backed by the process-local
/// <see cref="IMemoryCacheProvider"/>), built on <see cref="AbstractConnectionStore"/>. Register your own
/// <c>[Service(typeof(IConnectionStore))]</c> to store connections elsewhere. Application and framework services
/// should usually inject <see cref="ISocketManager"/> (an overridable facade over this store) instead of the store itself.
/// </remarks>
public interface IConnectionStore : ICleanUp
{
    /// <summary>Whether a connection with this id is stored (connected or not).</summary>
    /// <param name="connectionId">The connection id.</param>
    Task<bool> IsConnectionExistsAsync(string connectionId);
    /// <summary>
    /// Stores a connection under <paramref name="connectionId"/> (also assigning it as the connection's id) and,
    /// when <paramref name="roomId"/> is given, joins that room (which must already exist).
    /// </summary>
    /// <param name="connectionId">The connection id; empty/whitespace is rejected.</param>
    /// <param name="socket">The connection to store.</param>
    /// <param name="roomId">Optional room to join right away.</param>
    /// <returns><c>false</c> when the id is empty or the room does not exist; otherwise <c>true</c>.</returns>
    Task<bool> AddConnectionAsync(string connectionId, AltruistConnection socket, string? roomId = null);
    /// <summary>
    /// Removes the connection and its room membership; a room left empty is deleted (except the waiting room).
    /// Does not close the socket or run disconnect hooks; to disconnect a client use <see cref="IConnectionManager.DisconnectAsync"/>.
    /// </summary>
    /// <param name="connectionId">The connection id.</param>
    Task RemoveConnectionAsync(string connectionId);
    /// <summary>Returns the stored connection, or <c>null</c>. Check <see cref="AltruistConnection.IsConnected"/> before using it.</summary>
    /// <param name="connectionId">The connection id.</param>
    Task<AltruistConnection?> GetConnectionAsync(string connectionId);
    /// <summary>
    /// Returns a cursor over all stored connections (including ones no longer connected). Cheapest way to iterate;
    /// use <see cref="GetAllConnectionsDictAsync"/> for lookups by id or <see cref="GetAllConnectionIdsAsync"/> for ids only.
    /// </summary>
    Task<ICursor<AltruistConnection>> GetAllConnectionsAsync();
    /// <summary>Returns a snapshot of all stored connections keyed by connection id.</summary>
    Task<Dictionary<string, AltruistConnection>> GetAllConnectionsDictAsync();
    /// <summary>Returns a snapshot of all stored connection ids.</summary>
    Task<IEnumerable<string>> GetAllConnectionIdsAsync();
    /// <summary>Returns the room, or <c>null</c> when it does not exist.</summary>
    /// <param name="roomId">The room id.</param>
    Task<RoomPacket?> GetRoomAsync(string roomId);
    /// <summary>Returns all rooms keyed by room id (including the waiting room).</summary>
    Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync();
    /// <summary>
    /// Returns the stored connections of a room keyed by connection id (empty when the room does not exist). May
    /// include connections that are no longer connected; the senders skip those.
    /// </summary>
    /// <param name="roomId">The room id.</param>
    Task<Dictionary<string, AltruistConnection>> GetConnectionsInRoomAsync(string roomId);
    /// <summary>
    /// Returns the first room with fewer members than its max capacity, or <c>null</c>. Iteration order is unspecified
    /// and the waiting room is a candidate too.
    /// </summary>
    Task<RoomPacket?> FindAvailableRoomAsync();
    /// <summary>
    /// Moves a connection into a room: leaves its current room (deleting it if left empty, except the waiting room),
    /// adds it to <paramref name="roomId"/> and records the membership. Joining the room it is already in is a no-op.
    /// Create the room first with <see cref="CreateRoomAsync"/>.
    /// </summary>
    /// <param name="connectionId">The connection id.</param>
    /// <param name="roomId">The target room id.</param>
    /// <returns>The joined room, or <c>null</c> when the room does not exist.</returns>
    Task<RoomPacket?> JoinRoomAsync(string connectionId, string roomId);
    /// <summary>Returns the room the client is currently in, or <c>null</c>.</summary>
    /// <param name="clientId">The connection id.</param>
    Task<RoomPacket?> FindRoomForClientAsync(string clientId);
    /// <summary>Creates an empty room, or returns the existing room with the same id (idempotent).</summary>
    /// <param name="roomId">The room id; <c>null</c> generates a GUID.</param>
    /// <returns>The new or existing room.</returns>
    Task<RoomPacket> CreateRoomAsync(string? roomId);
    /// <summary>Persists a modified room (e.g. after changing its metadata). Membership is managed by <see cref="JoinRoomAsync"/>/<see cref="RemoveConnectionAsync"/>.</summary>
    /// <param name="room">The room to save (keyed by its id).</param>
    Task SaveRoomAsync(RoomPacket room);
    /// <summary>Deletes the room record. Members are not disconnected and keep their (now dangling) membership until they join another room or are removed.</summary>
    /// <param name="roomId">The room id.</param>
    Task DeleteRoomAsync(string roomId);
}


/// <summary>
/// Base <see cref="IConnectionStore"/> keeping connections, rooms and the connection-to-room mapping in an
/// <see cref="IMemoryCacheProvider"/>. Room membership changes are serialized with a lock, so it is safe under
/// concurrent connects/disconnects. Subclass it (every member is virtual) to customize one behaviour while keeping the rest.
/// </summary>
public abstract class AbstractConnectionStore : IConnectionStore
{
    /// <summary>The cache holding connections, rooms and memberships.</summary>
    protected readonly IMemoryCacheProvider _memoryCache;
    /// <summary>Logger for the store.</summary>
    protected readonly ILogger _logger;

    /// <summary>
    /// Serializes room membership changes. RoomPacket.ConnectionIds is a plain HashSet shared by
    /// every connection (all sockets start in the waiting room), and connects/disconnects run on
    /// many threads at once; unsynchronized Add/Remove corrupts the set under a connect storm.
    /// </summary>
    private readonly SemaphoreSlim _roomLock = new(1, 1);

    /// <summary>Creates the store over the given cache.</summary>
    /// <param name="cache">The process-local cache to store connections and rooms in.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public AbstractConnectionStore(IMemoryCacheProvider cache, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AbstractConnectionStore>();
        _memoryCache = cache;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public virtual async Task<AltruistConnection?> GetConnectionAsync(string connectionId)
    {
        return await _memoryCache.GetAsync<AltruistConnection>(connectionId);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public virtual async Task<ICursor<AltruistConnection>> GetAllConnectionsAsync()
    {
        return await _memoryCache.GetAllAsync<AltruistConnection>();
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public virtual async Task<RoomPacket?> GetRoomAsync(string roomId)
    {
        return await _memoryCache.GetAsync<RoomPacket>(roomId);

    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public virtual async Task DeleteRoomAsync(string roomId)
    {
        await _memoryCache.RemoveAndForgetAsync<RoomPacket>(roomId);
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public virtual async Task SaveRoomAsync(RoomPacket room)
    {
        await _memoryCache.SaveAsync(room.Id, room);
    }

    /// <summary>
    /// Removes stored connections that are no longer connected, prunes room members whose connection is gone (deleting
    /// rooms left empty), and re-creates the waiting room.
    /// </summary>
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

    /// <inheritdoc/>
    public virtual Task<bool> IsConnectionExistsAsync(string connectionId)
    {
        return _memoryCache.ContainsAsync<AltruistConnection>(connectionId);
    }
}
