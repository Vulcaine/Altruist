using Microsoft.Extensions.Logging;

namespace Altruist;

public interface ICleanUp
{
    Task Cleanup();
}

public interface IConnectionStore : ICleanUp
{
    Task<bool> AddConnection(string connectionId, IConnection socket, string? roomId = null);
    Task RemoveConnection(string connectionId);
    Task<IConnection?> GetConnection(string connectionId);
    Task<Dictionary<string, IConnection>> GetAllConnections();
    Task<IEnumerable<string>> GetAllConnectionIds();

    Task<RoomPacket?> GetRoomAsync(string roomId);
    Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync();
    Task<Dictionary<string, IConnection>> GetConnectionsInRoom(string roomId);
    Task<RoomPacket> FindAvailableRoomAsync();
    Task<RoomPacket?> AddClientToRoom(string connectionId, string roomId);
    Task<RoomPacket?> FindRoomForClientAsync(string clientId);
    Task<RoomPacket> CreateRoom();
    Task SaveRoom(RoomPacket room);
    Task DeleteRoomAsync(string roomId);
}


public abstract class AbstractConnectionStore : IConnectionStore
{
    protected readonly IMemoryCache _memoryCache;
    protected readonly ILogger _logger;

    public AbstractConnectionStore(IMemoryCache cache, ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AbstractConnectionStore>();
        _memoryCache = cache;
    }

    public virtual async Task<bool> AddConnection(string connectionId, IConnection socket, string? roomId = null)
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

    public virtual async Task RemoveConnection(string connectionId)
    {
        await _memoryCache.RemoveAsync<string>(connectionId);

        var roomId = await _memoryCache.GetAsync<string>(connectionId);
        if (!string.IsNullOrEmpty(roomId))
        {
            var room = await _memoryCache.GetAsync<RoomPacket>(roomId);
            if (room != null)
            {
                room.ConnectionIds.Remove(connectionId);
                if (room.ConnectionIds.Count == 0)
                {
                    await _memoryCache.RemoveAsync<RoomPacket>(roomId);
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

    public virtual async Task<IConnection?> GetConnection(string connectionId)
    {
        return await _memoryCache.GetAsync<IConnection>(connectionId);
    }

    public virtual async Task<Dictionary<string, IConnection>> GetAllConnections()
    {
        var connections = new Dictionary<string, IConnection>();

        var cursor = await _memoryCache.GetAllAsync<IConnection>();
        foreach (var connection in cursor)
        {
            connections[connection.ConnectionId] = connection;
        }

        return connections;
    }

    public virtual async Task<IEnumerable<string>> GetAllConnectionIds()
    {
        var connectionIds = new List<string>();

        var cursor = await _memoryCache.GetAllAsync<IConnection>();
        foreach (var connection in cursor)
        {
            connectionIds.Add(connection.ConnectionId);
        }

        return connectionIds;
    }

    public virtual async Task<Dictionary<string, IConnection>> GetConnectionsInRoom(string roomId)
    {
        var room = await _memoryCache.GetAsync<RoomPacket>(roomId);
        if (room != null)
        {
            var connectionsInRoom = new Dictionary<string, IConnection>();
            foreach (var connectionId in room.ConnectionIds)
            {
                var connection = await _memoryCache.GetAsync<IConnection>(connectionId);
                if (connection != null)
                {
                    connectionsInRoom[connectionId] = connection;
                }
            }

            return connectionsInRoom;
        }

        return new Dictionary<string, IConnection>();
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
            if (room.ConnectionIds.Count < 100)
            {
                return room;
            }
        }

        return await CreateRoom();
    }

    public virtual async Task DeleteRoomAsync(string roomId)
    {
        await _memoryCache.RemoveAsync<RoomPacket>(roomId);
    }

    public virtual async Task<RoomPacket> CreateRoom()
    {
        var roomId = $"{Guid.NewGuid()}";
        var room = new RoomPacket(roomId);

        await _memoryCache.SaveAsync(roomId, room);

        return room;
    }

    public virtual async Task<RoomPacket?> AddClientToRoom(string connectionId, string roomId)
    {
        var room = await GetRoomAsync(roomId);
        if (room == null)
        {
            return null;
        }
        await SaveRoom(room);
        await _memoryCache.SaveAsync(connectionId, roomId);
        return room;
    }

    public virtual async Task SaveRoom(RoomPacket room)
    {
        await _memoryCache.SaveAsync(room.Id, room);
    }

    public virtual async Task Cleanup()
    {
        var removed = new List<string>();
        var cursor = await _memoryCache.GetAllAsync<IConnection>();
        foreach (var connection in cursor)
        {
            if (!connection.IsConnected)
            {
                await RemoveConnection(connection.ConnectionId);
                removed.Add(connection.ConnectionId);
            }
        }

        if (removed.Count > 0)
        {
            _logger.LogInformation("Inactive connections have been removed from memory.");
        }
    }
}
