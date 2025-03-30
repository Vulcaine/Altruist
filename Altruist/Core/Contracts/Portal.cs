namespace Altruist;

public interface IPortalContext : IConnectionStore
{
    ICache Cache { get; }
    IAltruistRouter Router { get; }
    IMessageDecoder Decoder { get; }

    IAltruistContext AltruistContext { get; }
    IServiceProvider ServiceProvider { get; }

    IPlayerService<TPlayerEntity> GetPlayerService<TPlayerEntity>() where TPlayerEntity : PlayerEntity, new();

    void Initialize();
}


public abstract class AbstractSocketPortalContext : IPortalContext
{
    protected readonly IConnectionStore _connectionStore;
    public abstract IAltruistRouter Router { get; protected set; }
    public abstract IMessageDecoder Decoder { get; protected set; }

    public abstract IAltruistContext AltruistContext { get; protected set; }
    public abstract IServiceProvider ServiceProvider { get; protected set; }

    public abstract ICache Cache { get; protected set; }

    public AbstractSocketPortalContext(
        IAltruistContext altruistContext,
        IAltruistRouter router, IMessageDecoder decoder, IConnectionStore connectionStore, ICache cache, IServiceProvider serviceProvider)
    {
        AltruistContext = altruistContext;
        Decoder = decoder ?? new JsonMessageDecoder();
        _connectionStore = connectionStore;
        Router = router;
        ServiceProvider = serviceProvider;
        Cache = cache;
    }

    public abstract void Initialize();

    public async Task<Dictionary<string, IConnection>> GetConnectionsInRoom(string roomId)
    {
        return await _connectionStore.GetConnectionsInRoom(roomId);
    }

    public async Task<RoomPacket> FindAvailableRoom()
    {
        return await _connectionStore.FindAvailableRoom();
    }

    public async Task<RoomPacket> CreateRoom()
    {
        return await _connectionStore.CreateRoom();
    }

    public Task DeleteRoom(string roomName)
    {
        return _connectionStore.DeleteRoom(roomName);
    }

    public Task RemoveConnection(string connectionId)
    {
        return _connectionStore.RemoveConnection(connectionId);
    }

    public Task AddConnection(string connectionId, IConnection socket, string? roomId = null)
    {
        return _connectionStore.AddConnection(connectionId, socket, roomId);
    }

    public Task<IConnection?> GetConnection(string connectionId)
    {
        return _connectionStore.GetConnection(connectionId);
    }

    public Task<IEnumerable<string>> GetAllConnectionIds()
    {
        return _connectionStore.GetAllConnectionIds();
    }

    public Task<Dictionary<string, IConnection>> GetAllConnections()
    {
        return _connectionStore.GetAllConnections();
    }

    public async Task<RoomPacket> FindRoomForClient(string clientId)
    {
        return await _connectionStore.FindRoomForClient(clientId);
    }

    public abstract IPlayerService<TPlayerEntity> GetPlayerService<TPlayerEntity>() where TPlayerEntity : PlayerEntity, new();
    public async Task<RoomPacket> GetRoom(string roomId)
    {
        return await _connectionStore.GetRoom(roomId);
    }
    public async Task<Dictionary<string, RoomPacket>> GetAllRooms()
    {
        return await _connectionStore.GetAllRooms();
    }

    public async Task<RoomPacket> AddClientToRoom(string connectionId, string roomId)
    {
        return await _connectionStore.AddClientToRoom(connectionId, roomId);
    }

    public async Task SaveRoom(RoomPacket room)
    {
        await _connectionStore.SaveRoom(room);
    }

    public Task Cleanup()
    {
        return _connectionStore.Cleanup();
    }
}