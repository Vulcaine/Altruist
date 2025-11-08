using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

public interface ISocketManager
{
    Task<Dictionary<string, Connection>> GetConnectionsInRoomAsync(string roomId);
    Task<RoomPacket> FindAvailableRoomAsync();
    Task<RoomPacket> CreateRoomAsync();
    Task DeleteRoomAsync(string roomName);
    Task RemoveConnectionAsync(string connectionId);
    Task<bool> AddConnectionAsync(string connectionId, Connection socket, string? roomId = null);
    Task<Connection?> GetConnectionAsync(string connectionId);
    Task<IEnumerable<string>> GetAllConnectionIdsAsync();
    Task<ICursor<Connection>> GetAllConnectionsAsync();
    Task<RoomPacket?> FindRoomForClientAsync(string clientId);
    Task<RoomPacket?> GetRoomAsync(string roomId);
    Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync();
    Task<bool> IsConnectionExistsAsync(string connectionId);
    Task SaveRoomAsync(RoomPacket room);
    Task<RoomPacket?> AddClientToRoomAsync(string connectionId, string roomId);

    Task<Dictionary<string, Connection>> GetAllConnectionsDictAsync();

    Task Cleanup();
}

[Service(typeof(ISocketManager))]
public abstract class AbstractSocketManager : IConnectionStore, ISocketManager
{
    protected readonly IConnectionStore _connectionStore;

    // public ICodec Codec { get; }

    public AbstractSocketManager(IServiceProvider serviceProvider)
    {
        // Codec = serviceProvider.GetService<ICodec>() ?? new JsonCodec();
        _connectionStore = serviceProvider.GetRequiredService<IConnectionStore>();
        // Cache = serviceProvider.GetRequiredService<ICacheProvider>();
    }

    public abstract void Initialize();

    public virtual async Task<Dictionary<string, Connection>> GetConnectionsInRoomAsync(string roomId)
    {
        return await _connectionStore.GetConnectionsInRoomAsync(roomId);
    }

    public virtual async Task<RoomPacket> FindAvailableRoomAsync()
    {
        return await _connectionStore.FindAvailableRoomAsync();
    }

    public virtual async Task<RoomPacket> CreateRoomAsync()
    {
        return await _connectionStore.CreateRoomAsync();
    }

    public virtual Task DeleteRoomAsync(string roomName)
    {
        return _connectionStore.DeleteRoomAsync(roomName);
    }

    public virtual Task RemoveConnectionAsync(string connectionId)
    {
        return _connectionStore.RemoveConnectionAsync(connectionId);
    }

    public virtual Task<bool> AddConnectionAsync(string connectionId, Connection socket, string? roomId = null)
    {
        return _connectionStore.AddConnectionAsync(connectionId, socket, roomId);
    }

    public virtual Task<Connection?> GetConnectionAsync(string connectionId)
    {
        return _connectionStore.GetConnectionAsync(connectionId);
    }

    public virtual Task<IEnumerable<string>> GetAllConnectionIdsAsync()
    {
        return _connectionStore.GetAllConnectionIdsAsync();
    }

    public virtual Task<ICursor<Connection>> GetAllConnectionsAsync()
    {
        return _connectionStore.GetAllConnectionsAsync();
    }

    public virtual async Task<RoomPacket?> FindRoomForClientAsync(string clientId)
    {
        return await _connectionStore.FindRoomForClientAsync(clientId);
    }

    public virtual async Task<RoomPacket?> GetRoomAsync(string roomId)
    {
        return await _connectionStore.GetRoomAsync(roomId);
    }
    public virtual async Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync()
    {
        return await _connectionStore.GetAllRoomsAsync();
    }

    public virtual async Task<RoomPacket?> AddClientToRoomAsync(string connectionId, string roomId)
    {
        return await _connectionStore.AddClientToRoomAsync(connectionId, roomId);
    }

    public virtual async Task SaveRoomAsync(RoomPacket room)
    {
        await _connectionStore.SaveRoomAsync(room);
    }

    public virtual async Task Cleanup()
    {
        await _connectionStore.Cleanup();
    }

    public virtual async Task<bool> IsConnectionExistsAsync(string connectionId)
    {
        return await _connectionStore.IsConnectionExistsAsync(connectionId);
    }

    public virtual async Task<Dictionary<string, Connection>> GetAllConnectionsDictAsync()
    {
        return await _connectionStore.GetAllConnectionsDictAsync();
    }
}