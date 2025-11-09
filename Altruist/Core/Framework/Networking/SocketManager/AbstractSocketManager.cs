using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

public interface ISocketManager
{
    Task<Dictionary<string, AltruistConnection>> GetConnectionsInRoomAsync(string roomId);
    Task<RoomPacket> FindAvailableRoomAsync();
    Task<RoomPacket> CreateRoomAsync();
    Task DeleteRoomAsync(string roomName);
    Task RemoveConnectionAsync(string connectionId);
    Task<bool> AddConnectionAsync(string connectionId, AltruistConnection socket, string? roomId = null);
    Task<AltruistConnection?> GetConnectionAsync(string connectionId);
    Task<IEnumerable<string>> GetAllConnectionIdsAsync();
    Task<ICursor<AltruistConnection>> GetAllConnectionsAsync();
    Task<RoomPacket?> FindRoomForClientAsync(string clientId);
    Task<RoomPacket?> GetRoomAsync(string roomId);
    Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync();
    Task<bool> IsConnectionExistsAsync(string connectionId);
    Task SaveRoomAsync(RoomPacket room);
    Task<RoomPacket?> AddClientToRoomAsync(string connectionId, string roomId);

    Task<Dictionary<string, AltruistConnection>> GetAllConnectionsDictAsync();

    Task Cleanup();
}

[Service(typeof(ISocketManager))]
public class SocketManager : IConnectionStore, ISocketManager
{
    protected readonly IConnectionStore _connectionStore;

    // public ICodec Codec { get; }

    public SocketManager(IServiceProvider serviceProvider)
    {
        // Codec = serviceProvider.GetService<ICodec>() ?? new JsonCodec();
        _connectionStore = serviceProvider.GetRequiredService<IConnectionStore>();
        // Cache = serviceProvider.GetRequiredService<ICacheProvider>();
    }

    public virtual void Initialize()
    {

    }

    public virtual async Task<Dictionary<string, AltruistConnection>> GetConnectionsInRoomAsync(string roomId)
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

    public virtual Task<bool> AddConnectionAsync(string connectionId, AltruistConnection socket, string? roomId = null)
    {
        return _connectionStore.AddConnectionAsync(connectionId, socket, roomId);
    }

    public virtual Task<AltruistConnection?> GetConnectionAsync(string connectionId)
    {
        return _connectionStore.GetConnectionAsync(connectionId);
    }

    public virtual Task<IEnumerable<string>> GetAllConnectionIdsAsync()
    {
        return _connectionStore.GetAllConnectionIdsAsync();
    }

    public virtual Task<ICursor<AltruistConnection>> GetAllConnectionsAsync()
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

    public virtual async Task<Dictionary<string, AltruistConnection>> GetAllConnectionsDictAsync()
    {
        return await _connectionStore.GetAllConnectionsDictAsync();
    }
}