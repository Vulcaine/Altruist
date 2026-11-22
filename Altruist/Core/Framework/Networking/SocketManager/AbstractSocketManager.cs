namespace Altruist;

/// <summary>
/// Connection and room registry used by the networking layer: which client connections exist, and which room
/// (group of connections) each one is in. Inject this to look up a client's <see cref="AltruistConnection"/>
/// or the members of a room.
/// </summary>
/// <remarks>
/// The default implementation, <see cref="SocketManager"/>, is a thin overridable facade over the registered
/// <see cref="IConnectionStore"/> (which owns the actual state, e.g. the in-memory store). Inject
/// <see cref="ISocketManager"/> in application/framework services; replace <see cref="IConnectionStore"/> to change
/// where connections and rooms are stored. To send packets, prefer the outbound senders (e.g. <c>ClientSender</c>)
/// over writing to connections directly.
/// Every connection starts in the waiting room (<c>StoreConstants.WaitingRoomId</c>); a connection is in at most one room.
/// </remarks>
public interface ISocketManager
{
    /// <summary>Returns the live connections of a room keyed by connection id (empty when the room does not exist).</summary>
    /// <param name="roomId">Room id.</param>
    Task<Dictionary<string, AltruistConnection>> GetConnectionsInRoomAsync(string roomId);
    /// <summary>Returns the first room with free capacity (fewer members than its max capacity), or <c>null</c>.</summary>
    Task<RoomPacket?> FindAvailableRoomAsync();
    /// <summary>Creates a room, or returns the existing one with the same id.</summary>
    /// <param name="roomId">Room id; <c>null</c> generates a GUID.</param>
    Task<RoomPacket> CreateRoomAsync(string? roomId);
    /// <summary>Deletes a room record (members are not disconnected).</summary>
    /// <param name="roomName">Room id.</param>
    Task DeleteRoomAsync(string roomName);
    /// <summary>Removes a connection and its room membership; empty rooms other than the waiting room are deleted.</summary>
    /// <param name="connectionId">Connection id.</param>
    Task RemoveConnectionAsync(string connectionId);
    /// <summary>Registers a connection under <paramref name="connectionId"/> and optionally joins it to a room.</summary>
    /// <param name="connectionId">Connection id (assigned to the connection).</param>
    /// <param name="socket">Connection object.</param>
    /// <param name="roomId">Optional room to join.</param>
    /// <returns><c>false</c> when the id is blank or the room join failed.</returns>
    Task<bool> AddConnectionAsync(string connectionId, AltruistConnection socket, string? roomId = null);
    /// <summary>Returns the connection with the given id, or <c>null</c>.</summary>
    /// <param name="connectionId">Connection id.</param>
    Task<AltruistConnection?> GetConnectionAsync(string connectionId);
    /// <summary>Returns the ids of all registered connections.</summary>
    Task<IEnumerable<string>> GetAllConnectionIdsAsync();
    /// <summary>Returns a cursor over all registered connections (streams; prefer it for large counts).</summary>
    Task<ICursor<AltruistConnection>> GetAllConnectionsAsync();
    /// <summary>Returns the room the client is currently in, or <c>null</c>.</summary>
    /// <param name="clientId">Connection id.</param>
    Task<RoomPacket?> FindRoomForClientAsync(string clientId);
    /// <summary>Returns a room by id, or <c>null</c>.</summary>
    /// <param name="roomId">Room id.</param>
    Task<RoomPacket?> GetRoomAsync(string roomId);
    /// <summary>Returns all rooms keyed by id.</summary>
    Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync();
    /// <summary>Returns whether a connection with this id is registered.</summary>
    /// <param name="connectionId">Connection id.</param>
    Task<bool> IsConnectionExistsAsync(string connectionId);
    /// <summary>Persists changes made to a room object.</summary>
    /// <param name="room">Room to save.</param>
    Task SaveRoomAsync(RoomPacket room);
    /// <summary>Moves a connection into a room, leaving its previous room. Returns the joined room, or <c>null</c> on failure.</summary>
    /// <param name="connectionId">Connection id.</param>
    /// <param name="roomId">Target room id.</param>
    Task<RoomPacket?> JoinRoomAsync(string connectionId, string roomId);

    /// <summary>Returns all connections keyed by connection id (materialized; use <see cref="GetAllConnectionsAsync"/> to stream).</summary>
    Task<Dictionary<string, AltruistConnection>> GetAllConnectionsDictAsync();

    /// <summary>Releases/cleans store state (delegates to <see cref="ICleanUp.Cleanup"/> of the store).</summary>
    Task Cleanup();
}

/// <summary>
/// Default <see cref="ISocketManager"/> (registered as a singleton service when <c>altruist:server:transport</c> is configured).
/// Every member is virtual and simply delegates to the injected <see cref="IConnectionStore"/>; subclass it to add
/// behaviour around connection/room changes.
/// </summary>
[Service(typeof(ISocketManager))]
[ConditionalOnConfig("altruist:server:transport")]
public class SocketManager : IConnectionStore, ISocketManager
{
    /// <summary>Underlying store all calls are delegated to.</summary>
    protected readonly IConnectionStore _connectionStore;

    /// <summary>Creates the manager over a connection store.</summary>
    /// <param name="connectionStore">Store that owns connection and room state.</param>
    public SocketManager(IConnectionStore connectionStore)
    {
        _connectionStore = connectionStore;
    }

    /// <summary>Initialization hook; no-op by default.</summary>
    public virtual void Initialize()
    {

    }

    /// <inheritdoc/>
    public virtual async Task<Dictionary<string, AltruistConnection>> GetConnectionsInRoomAsync(string roomId)
    {
        return await _connectionStore.GetConnectionsInRoomAsync(roomId);
    }

    /// <inheritdoc/>
    public virtual async Task<RoomPacket?> FindAvailableRoomAsync()
    {
        return await _connectionStore.FindAvailableRoomAsync();
    }

    /// <inheritdoc/>
    public virtual async Task<RoomPacket> CreateRoomAsync(string? roomId = null)
    {
        return await _connectionStore.CreateRoomAsync(roomId);
    }

    /// <inheritdoc/>
    public virtual Task DeleteRoomAsync(string roomName)
    {
        return _connectionStore.DeleteRoomAsync(roomName);
    }

    /// <inheritdoc/>
    public virtual Task RemoveConnectionAsync(string connectionId)
    {
        return _connectionStore.RemoveConnectionAsync(connectionId);
    }

    /// <inheritdoc/>
    public virtual Task<bool> AddConnectionAsync(string connectionId, AltruistConnection socket, string? roomId = null)
    {
        return _connectionStore.AddConnectionAsync(connectionId, socket, roomId);
    }

    /// <inheritdoc/>
    public virtual Task<AltruistConnection?> GetConnectionAsync(string connectionId)
    {
        return _connectionStore.GetConnectionAsync(connectionId);
    }

    /// <inheritdoc/>
    public virtual Task<IEnumerable<string>> GetAllConnectionIdsAsync()
    {
        return _connectionStore.GetAllConnectionIdsAsync();
    }

    /// <inheritdoc/>
    public virtual Task<ICursor<AltruistConnection>> GetAllConnectionsAsync()
    {
        return _connectionStore.GetAllConnectionsAsync();
    }

    /// <inheritdoc/>
    public virtual async Task<RoomPacket?> FindRoomForClientAsync(string clientId)
    {
        return await _connectionStore.FindRoomForClientAsync(clientId);
    }

    /// <inheritdoc/>
    public virtual async Task<RoomPacket?> GetRoomAsync(string roomId)
    {
        return await _connectionStore.GetRoomAsync(roomId);
    }
    /// <inheritdoc/>
    public virtual async Task<Dictionary<string, RoomPacket>> GetAllRoomsAsync()
    {
        return await _connectionStore.GetAllRoomsAsync();
    }

    /// <inheritdoc/>
    public virtual async Task<RoomPacket?> JoinRoomAsync(string connectionId, string roomId)
    {
        return await _connectionStore.JoinRoomAsync(connectionId, roomId);
    }

    /// <inheritdoc/>
    public virtual async Task SaveRoomAsync(RoomPacket room)
    {
        await _connectionStore.SaveRoomAsync(room);
    }

    /// <inheritdoc/>
    public virtual async Task Cleanup()
    {
        await _connectionStore.Cleanup();
    }

    /// <inheritdoc/>
    public virtual async Task<bool> IsConnectionExistsAsync(string connectionId)
    {
        return await _connectionStore.IsConnectionExistsAsync(connectionId);
    }

    /// <inheritdoc/>
    public virtual async Task<Dictionary<string, AltruistConnection>> GetAllConnectionsDictAsync()
    {
        return await _connectionStore.GetAllConnectionsDictAsync();
    }
}
