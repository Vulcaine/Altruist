using System.Collections.Concurrent;

using Altruist;

using Microsoft.Extensions.Logging;

public sealed class GameSessionResult
{
    public List<IPacketBase> ClientPackets { get; } = new();
    public List<RoomBroadcast> RoomBroadcasts { get; } = new();

    public static GameSessionResult Empty => new GameSessionResult();
}

public interface IGameSessionService
{
    void ClearAllContexts(string clientId);

    // Core session lifecycle
    Task Cleanup();

    /// <summary>
    /// Exit the game:
    ///   - returns a RoomBroadcast (if the player was in a room)
    ///   - returns null if no broadcast is needed (e.g. client not in any room).
    /// </summary>
    Task<RoomBroadcast?> ExitGameAsync(LeaveGamePacket message, string clientId);

    /// <summary>
    /// Join game:
    ///   - on failure: ResultPacket(FailedPacket)
    ///   - on success: ResultPacket(SuccessPacket) or other dedicated packet
    /// </summary>
    Task<IResultPacket> JoinGameAsync(JoinGamePacket message, string clientId);

    /// <summary>
    /// Handshake:
    ///   - returns ResultPacket(HandshakePacket) or other dedicated packet.
    /// </summary>
    Task<IResultPacket> HandshakeAsync(HandshakeRequestPacket message, string clientId);

    // ---- Minimal Session Context API (type-only) ----
    Task SetContext<T>(string clientId, T value);
    Task<T?> GetContext<T>(string clientId);
    Task RemoveContext<T>(string clientId);
}

[Service(typeof(IGameSessionService))]
public class GameSessionService : IGameSessionService
{
    private readonly ISocketManager _socketManager;
    private readonly ILogger _logger;

    private readonly ConcurrentDictionary<string, List<object>> _contexts =
        new(StringComparer.Ordinal);

    public GameSessionService(
        ISocketManager socketManager,
        ILoggerFactory loggerFactory)
    {
        _socketManager = socketManager;
        _logger = loggerFactory.CreateLogger(GetType());
    }

    // -------------------------
    // Session lifecycle methods
    // -------------------------

    public virtual async Task<IResultPacket> HandshakeAsync(
        HandshakeRequestPacket message,
        string clientId)
    {
        var rooms = await _socketManager.GetAllRoomsAsync();

        var responsePacket = new HandshakeResponsePacket(rooms.Values.ToArray());

        return ResultPacket.Success(TransportCode.Accepted, responsePacket);
    }

    public virtual async Task<RoomBroadcast?> ExitGameAsync(
        LeaveGamePacket message,
        string clientId)
    {
        // Find room for client (if any)
        var room = await _socketManager.FindRoomForClientAsync(clientId);

        if (room == null)
        {
            // No room → nothing to broadcast, just clear context and return null.
            ClearAllContexts(clientId);
            return null;
        }

        // Remove client from room & persist
        room = room.RemoveConnection(clientId);
        await _socketManager.SaveRoomAsync(room);

        // Build broadcast packet to the room
        var broadcastPacket = new LeaveGamePacket(clientId);

        // Optionally delete empty room
        if (room.Empty())
        {
            await _socketManager.DeleteRoomAsync(room.Id);
        }

        // Clear contexts on exit (optional — comment out to keep sticky contexts)
        ClearAllContexts(clientId);

        return new RoomBroadcast(room.Id, broadcastPacket);
    }

    public virtual async Task<IResultPacket> JoinGameAsync(
        JoinGamePacket message,
        string clientId)
    {
        if (string.IsNullOrEmpty(message.Name))
        {
            // failure → ResultPacket(FailedPacket)
            return ResultPacket.Failed(
                TransportCode.BadRequest,
                "Username is required!");
        }

        RoomPacket? room;
        if (!string.IsNullOrEmpty(message.RoomId))
        {
            room = await _socketManager.GetRoomAsync(message.RoomId);
            if (room == null)
            {
                var joinFailedMsg = $"Join failed. No such room: {message.RoomId}";
                return ResultPacket.Failed(TransportCode.BadRequest, joinFailedMsg);
            }
        }
        else
        {
            room = await _socketManager.FindAvailableRoomAsync();
        }

        if (room == null)
        {
            var msg = "Join failed: No available rooms";
            _logger.LogWarning(msg);
            return ResultPacket.Failed(
                TransportCode.BadRequest,
                msg);
        }

        if (room.Has(clientId))
        {
            var msg = $"Join failed: {clientId} is already in the game";
            _logger.LogWarning(msg);
            return ResultPacket.Failed(TransportCode.BadRequest, msg);
        }

        // Success branch – for now we just return a SuccessPacket.
        var successMsg = $"Player {message.Name} joined the room: {room.Id}.";
        _logger.LogInformation(successMsg);

        // Success → ResultPacket(SuccessPacket)
        return ResultPacket.Success(TransportCode.BadRequest, successMsg);
    }

    public async Task Cleanup()
    {
        try
        {
            _contexts.Clear();
            await _socketManager.Cleanup();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up connections and contexts.");
        }
    }


    // -------------------------
    // Minimal type-only context API
    // -------------------------

    public Task SetContext<T>(string clientId, T value)
    {
        var list = _contexts.GetOrAdd(clientId, _ => new List<object>());
        lock (list)
        {
            // ensure only one instance per type (last write wins)
            for (int i = list.Count - 1; i >= 0; --i)
            {
                if (list[i] is T)
                { list.RemoveAt(i); break; }
            }
            list.Add(value!);
        }
        return Task.CompletedTask;
    }

    public Task<T?> GetContext<T>(string clientId)
    {
        if (_contexts.TryGetValue(clientId, out var list))
        {
            lock (list)
            {
                // return the most recently added of type T (if any)
                for (int i = list.Count - 1; i >= 0; --i)
                {
                    if (list[i] is T t)
                        return Task.FromResult<T?>(t);
                }
            }
        }
        return Task.FromResult<T?>(default);
    }

    public Task RemoveContext<T>(string clientId)
    {
        if (_contexts.TryGetValue(clientId, out var list))
        {
            lock (list)
            {
                for (int i = list.Count - 1; i >= 0; --i)
                {
                    if (list[i] is T)
                    { list.RemoveAt(i); break; }
                }
                if (list.Count == 0)
                    _contexts.TryRemove(clientId, out _);
            }
        }
        return Task.CompletedTask;
    }

    public void ClearAllContexts(string clientId)
    {
        _contexts.TryRemove(clientId, out _);
    }
}
