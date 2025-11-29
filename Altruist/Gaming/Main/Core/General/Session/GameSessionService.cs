using System.Collections.Concurrent;

using Altruist;

using Microsoft.Extensions.Logging;

public sealed class GameSessionResult
{
    public List<IPacketBase> ClientPackets { get; } = new();
    public List<RoomBroadcast> RoomBroadcasts { get; } = new();

    public static GameSessionResult Empty => new GameSessionResult();
}

public interface IGameSession
{
    /// <summary>
    /// The logical id of this session (e.g. accountId, userId, etc.)
    /// </summary>
    string Id { get; }

    /// <summary>
    /// Store a context object inside this session, bound to a context id and type.
    /// Only one instance per (context id, type) is kept (last write wins).
    /// </summary>
    Task SetContext<T>(string id, T value);

    /// <summary>
    /// Get the most recently stored context of type T for the given context id (if any).
    /// </summary>
    Task<T?> GetContext<T>(string id);

    /// <summary>
    /// Remove a context of type T for the given context id (if any).
    /// </summary>
    Task RemoveContext<T>(string id);

    /// <summary>
    /// Remove all contexts for this session (for all inner ids).
    /// </summary>
    void ClearAllContexts();
}

public interface IGameSessionService
{
    // -------------------------
    // Session object API
    // -------------------------

    /// <summary>
    /// Get or create a session for the given global session id (e.g. accountId).
    ///   var session = _gameSessionService.SetSession(globalSessionId);
    ///   await session.SetContext(innerId, value);
    /// </summary>
    IGameSession SetSession(string sessionId);

    /// <summary>
    /// Get an existing session if it exists; returns null otherwise.
    /// </summary>
    IGameSession? GetSession(string sessionId);

    /// <summary>
    /// Clear all contexts for a given session id and remove the session.
    /// This disregards what contexts are inside; everything is cleared.
    /// </summary>
    void ClearSession(string sessionId);

    // -------------------------
    // Core session lifecycle
    // -------------------------

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
}

internal sealed class GameSession : IGameSession
{
    // contexts[innerId] = list of objects (type-based, last write wins per type)
    private readonly Dictionary<string, List<object>> _contexts = new();
    private readonly object _lock = new();

    public string Id { get; }

    public GameSession(string id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
    }

    public Task SetContext<T>(string id, T value)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Context id must be non-empty.", nameof(id));

        lock (_lock)
        {
            if (!_contexts.TryGetValue(id, out var list))
            {
                list = new List<object>();
                _contexts[id] = list;
            }

            // ensure only one instance per type (last write wins) for this context id
            for (int i = list.Count - 1; i >= 0; --i)
            {
                if (list[i] is T)
                {
                    list.RemoveAt(i);
                    break;
                }
            }

            list.Add(value!);
        }

        return Task.CompletedTask;
    }

    public Task<T?> GetContext<T>(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Task.FromResult<T?>(default);

        lock (_lock)
        {
            if (_contexts.TryGetValue(id, out var list))
            {
                for (int i = list.Count - 1; i >= 0; --i)
                {
                    if (list[i] is T t)
                    {
                        return Task.FromResult<T?>(t);
                    }
                }
            }
        }

        return Task.FromResult<T?>(default);
    }

    public Task RemoveContext<T>(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return Task.CompletedTask;

        lock (_lock)
        {
            if (_contexts.TryGetValue(id, out var list))
            {
                for (int i = list.Count - 1; i >= 0; --i)
                {
                    if (list[i] is T)
                    {
                        list.RemoveAt(i);
                        break;
                    }
                }

                if (list.Count == 0)
                {
                    _contexts.Remove(id);
                }
            }
        }

        return Task.CompletedTask;
    }

    public void ClearAllContexts()
    {
        lock (_lock)
        {
            _contexts.Clear();
        }
    }
}

[Service(typeof(IGameSessionService))]
public class GameSessionService : IGameSessionService
{
    private readonly ISocketManager _socketManager;
    private readonly ILogger _logger;

    // All sessions are keyed by a global session id (e.g. accountId, userId, etc.)
    private readonly ConcurrentDictionary<string, GameSession> _sessions =
        new(StringComparer.Ordinal);

    public GameSessionService(
        ISocketManager socketManager,
        ILoggerFactory loggerFactory)
    {
        _socketManager = socketManager;
        _logger = loggerFactory.CreateLogger(GetType());
    }

    // -------------------------
    // Session object API
    // -------------------------

    public IGameSession SetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id must be non-empty.", nameof(sessionId));

        return _sessions.GetOrAdd(sessionId, id => new GameSession(id));
    }

    public IGameSession? GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        return _sessions.TryGetValue(sessionId, out var session)
            ? session
            : null;
    }

    public void ClearSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return;

        if (_sessions.TryRemove(sessionId, out var session))
        {
            // Disregard internal structure; just nuke all contexts in this session.
            session.ClearAllContexts();
        }
    }

    // -------------------------
    // Core session lifecycle methods
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
            // No room → nothing to broadcast, just clear session for this id and return null.
            ClearSession(clientId);
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
        ClearSession(clientId);

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
            _sessions.Clear();
            await _socketManager.Cleanup();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up connections and contexts.");
        }
    }
}
