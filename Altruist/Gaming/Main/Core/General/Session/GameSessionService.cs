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
    /// Store a context object for this session, keyed by type.
    /// Only one instance per type is kept (last write wins).
    /// </summary>
    Task SetContext<T>(T value);

    /// <summary>
    /// Get the most recently stored context of type T (if any).
    /// </summary>
    Task<T?> GetContext<T>();

    /// <summary>
    /// Remove a context of type T for this session (if any).
    /// </summary>
    Task RemoveContext<T>();

    /// <summary>
    /// Remove all contexts for this session.
    /// </summary>
    void ClearAllContexts();
}

public interface IGameSessionService
{
    // -------------------------
    // Session object API
    // -------------------------

    /// <summary>
    /// Get or create a session for the given id (e.g. accountId).
    /// This is the main entry point:
    ///   var session = _gameSessionService.SetSession(accountId);
    ///   await session.SetContext(...);
    /// </summary>
    IGameSession SetSession(string sessionId);

    /// <summary>
    /// Get an existing session if it exists; returns null otherwise.
    /// </summary>
    IGameSession? GetSession(string sessionId);

    /// <summary>
    /// Clear all contexts for a given session id and remove the session.
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

    // -------------------------
    // Legacy minimal context API (string key)
    // These are convenience wrappers around sessions, so you can still use:
    //   await _gameSessionService.SetContext(accountId, value);
    // while internally using GameSession.
    // -------------------------

    Task SetContext<T>(string clientId, T value);
    Task<T?> GetContext<T>(string clientId);
    Task RemoveContext<T>(string clientId);
}

internal sealed class GameSession : IGameSession
{
    private readonly List<object> _contexts = new();
    private readonly object _lock = new();

    public string Id { get; }

    public GameSession(string id)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
    }

    public Task SetContext<T>(T value)
    {
        lock (_lock)
        {
            // ensure only one instance per type (last write wins)
            for (int i = _contexts.Count - 1; i >= 0; --i)
            {
                if (_contexts[i] is T)
                {
                    _contexts.RemoveAt(i);
                    break;
                }
            }

            _contexts.Add(value!);
        }

        return Task.CompletedTask;
    }

    public Task<T?> GetContext<T>()
    {
        lock (_lock)
        {
            for (int i = _contexts.Count - 1; i >= 0; --i)
            {
                if (_contexts[i] is T t)
                {
                    return Task.FromResult<T?>(t);
                }
            }
        }

        return Task.FromResult<T?>(default);
    }

    public Task RemoveContext<T>()
    {
        lock (_lock)
        {
            for (int i = _contexts.Count - 1; i >= 0; --i)
            {
                if (_contexts[i] is T)
                {
                    _contexts.RemoveAt(i);
                    break;
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

    // All sessions are keyed by a common id (e.g. accountId, userId, etc.)
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

    // -------------------------
    // Legacy minimal type-only context API
    // -------------------------

    public Task SetContext<T>(string clientId, T value)
    {
        var session = SetSession(clientId);
        return session.SetContext(value);
    }

    public Task<T?> GetContext<T>(string clientId)
    {
        var session = GetSession(clientId);
        return session != null
            ? session.GetContext<T>()
            : Task.FromResult<T?>(default);
    }

    public Task RemoveContext<T>(string clientId)
    {
        var session = GetSession(clientId);
        if (session == null)
            return Task.CompletedTask;

        return session.RemoveContext<T>();
    }
}
