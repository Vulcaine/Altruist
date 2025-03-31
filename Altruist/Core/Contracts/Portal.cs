namespace Altruist;

public interface IPortalContext : IConnectionStore
{
    ICache Cache { get; }
    IAltruistRouter Router { get; }
    ICodec Codec { get; }

    IAltruistContext AltruistContext { get; }
    IServiceProvider ServiceProvider { get; }

    IPlayerService<TPlayerEntity> GetPlayerService<TPlayerEntity>() where TPlayerEntity : PlayerEntity, new();

    void Initialize();
}


public abstract class AbstractSocketPortalContext : IPortalContext
{
    protected readonly IConnectionStore _connectionStore;
    public abstract IAltruistRouter Router { get; protected set; }
    public abstract ICodec Codec { get; protected set; }

    public abstract IAltruistContext AltruistContext { get; protected set; }
    public abstract IServiceProvider ServiceProvider { get; protected set; }

    public abstract ICache Cache { get; protected set; }

    public AbstractSocketPortalContext(
        IAltruistContext altruistContext,
        IAltruistRouter router, ICodec codec, IConnectionStore connectionStore, ICache cache, IServiceProvider serviceProvider)
    {
        AltruistContext = altruistContext;
        Codec = codec ?? new JsonCodec();
        _connectionStore = connectionStore;
        Router = router;
        ServiceProvider = serviceProvider;
        Cache = cache;
    }

    public abstract void Initialize();

    public async Task<Dictionary<string, IConnection>> GetConnectionsInRoomAsync(string roomId)
    {
        return await _connectionStore.GetConnectionsInRoomAsync(roomId);
    }

    public async Task<RoomPacket> FindAvailableRoomAsync()
    {
        return await _connectionStore.FindAvailableRoomAsync();
    }

    public async Task<RoomPacket> CreateRoomAsync()
    {
        return await _connectionStore.CreateRoomAsync();
    }

    public Task DeleteRoomAsync(string roomName)
    {
        return _connectionStore.DeleteRoomAsync(roomName);
    }

    public Task RemoveConnectionAsync(string connectionId)
    {
        return _connectionStore.RemoveConnectionAsync(connectionId);
    }

    public Task<bool> AddConnectionAsync(string connectionId, IConnection socket, string? roomId = null)
    {
        return _connectionStore.AddConnectionAsync(connectionId, socket, roomId);
    }

    public Task<IConnection?> GetConnectionAsync(string connectionId)
    {
        return _connectionStore.GetConnectionAsync(connectionId);
    }

    public Task<IEnumerable<string>> GetAllConnectionIdsAsync()
    {
        return _connectionStore.GetAllConnectionIdsAsync();
    }

    public Task<Dictionary<string, IConnection>> GetAllConnectionsAsync()
    {
        return _connectionStore.GetAllConnectionsAsync();
    }

    public async Task<RoomPacket?> FindRoomForClientAsync(string clientId)
    {
        return await _connectionStore.FindRoomForClientAsync(clientId);
    }

    public abstract IPlayerService<TPlayerEntity> GetPlayerService<TPlayerEntity>() where TPlayerEntity : PlayerEntity, new();
    public async Task<RoomPacket?> GetRoomAsync(string roomId)
    {
        return await _connectionStore.GetRoomAsync(roomId);
    }
    public async Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync()
    {
        return await _connectionStore.GetAllRoomsAsync();
    }

    public async Task<RoomPacket?> AddClientToRoomAsync(string connectionId, string roomId)
    {
        return await _connectionStore.AddClientToRoomAsync(connectionId, roomId);
    }

    public async Task SaveRoomAsync(RoomPacket room)
    {
        await _connectionStore.SaveRoomAsync(room);
    }

    public Task Cleanup()
    {
        return _connectionStore.Cleanup();
    }
}