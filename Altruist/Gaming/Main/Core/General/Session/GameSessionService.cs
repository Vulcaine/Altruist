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
    /// When this session expires (UTC).
    /// </summary>
    DateTime ExpiresAtUtc { get; }

    /// <summary>
    /// Extend / change the expiry time of this session.
    /// </summary>
    void Renew(DateTime newExpiresAtUtc);

    /// <summary>
    /// Whether this session is expired at the time of the call.
    /// </summary>
    bool Expired();

    /// <summary>
    /// Store a context object inside this session, bound to a context id and type.
    /// Only one instance per (context id, type) is kept (last write wins).
    /// </summary>
    Task SetContext<T>(string id, T value) where T : class;

    /// <summary>
    /// Get the most recently stored context of type T for the given context id (if any).
    /// </summary>
    Task<T?> GetContext<T>(string id) where T : class;

    IEnumerable<T> FindAllContexts<T>() where T : class;

    IEnumerable<object> FindAllContexts();

    /// <summary>
    /// Remove a context of type T for the given context id (if any).
    /// </summary>
    Task RemoveContext<T>(string id) where T : class;

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
    /// Create or get a session for the given global session id (e.g. accountId),
    /// with a specific expiry time (UTC).
    ///   var session = _gameSessionService.CreateSession(globalSessionId, expiresAtUtc);
    ///   await session.SetContext(innerId, value);
    /// If a non-expired session already exists, its expiry is renewed to the given time.
    /// If an expired session exists, it is cleared and replaced with a new one.
    /// </summary>
    IGameSession CreateSession(string sessionId, DateTime expiresAtUtc);

    /// <summary>
    /// Get an existing session if it exists and is not expired; returns null otherwise.
    /// If the session exists but is expired, it is cleaned up and removed.
    /// </summary>
    IGameSession? GetSession(string sessionId);

    /// <summary>
    /// Clear all contexts for a given session id and remove the session.
    /// This disregards what contexts are inside; everything is cleared.
    /// </summary>
    void ClearSession(string sessionId);

    /// <summary>
    /// Migrate all contexts from one session id to another.
    /// Creates or renews the target session with the given expiry,
    /// moves all contexts, and removes the source session.
    /// </summary>
    IGameSession? MigrateSession(string fromSessionId, string toSessionId, DateTime newExpiresAtUtc);

    // -------------------------
    // Core session lifecycle
    // -------------------------

    /// <summary>
    /// Remove expired sessions and cleanup socket state.
    /// Does NOT remove non-expired sessions.
    /// </summary>
    Task Cleanup();

    IEnumerable<T> FindAllContexts<T>() where T : class;

    IEnumerable<object> FindAllContexsts(string sessionId);

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

    private DateTime _expiresAtUtc;

    public string Id { get; }

    public DateTime ExpiresAtUtc
    {
        get
        {
            lock (_lock)
            {
                return _expiresAtUtc;
            }
        }
    }

    public GameSession(string id, DateTime expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Session id must be non-empty.", nameof(id));

        Id = id;
        _expiresAtUtc = expiresAtUtc;
    }

    public void Renew(DateTime newExpiresAtUtc)
    {
        var now = DateTime.UtcNow;
        if (newExpiresAtUtc <= now)
            throw new ArgumentException("New expiry must be in the future (UTC).", nameof(newExpiresAtUtc));

        lock (_lock)
        {
            _expiresAtUtc = newExpiresAtUtc;
        }
    }

    public bool Expired()
    {
        lock (_lock)
        {
            return DateTime.UtcNow >= _expiresAtUtc;
        }
    }

    public Task SetContext<T>(string id, T value) where T : class
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

    public Task<T?> GetContext<T>(string id) where T : class
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

        return Task.FromResult<T?>(null);
    }

    public Task RemoveContext<T>(string id) where T : class
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

    /// <summary>
    /// Move all contexts from this session into the target session.
    /// After this call, this session's contexts are cleared.
    /// For any (innerId, type) that exists on target, the values from this session win (last write wins).
    /// </summary>
    internal void MoveAllContextsTo(GameSession target)
    {
        if (target is null || ReferenceEquals(this, target))
            return;

        // simple nested locking; if you ever migrate both ways concurrently,
        // you might want a more sophisticated lock ordering
        lock (_lock)
        {
            lock (target._lock)
            {
                foreach (var kv in _contexts)
                {
                    var innerId = kv.Key;
                    var sourceList = kv.Value;

                    if (!target._contexts.TryGetValue(innerId, out var targetList))
                    {
                        targetList = new List<object>();
                        target._contexts[innerId] = targetList;
                    }

                    foreach (var obj in sourceList)
                    {
                        var objType = obj.GetType();

                        // remove existing of same type in target for this innerId
                        for (int i = targetList.Count - 1; i >= 0; --i)
                        {
                            if (targetList[i].GetType() == objType)
                            {
                                targetList.RemoveAt(i);
                                break;
                            }
                        }

                        targetList.Add(obj);
                    }
                }

                _contexts.Clear();
            }
        }
    }

    public IEnumerable<T> FindAllContexts<T>() where T : class
    {
        lock (_lock)
        {
            return _contexts.Values
                .SelectMany(list => list)
                .OfType<T>();
        }
    }

    public IEnumerable<object> FindAllContexts()
    {
        lock (_lock)
        {
            return _contexts.Values
                .SelectMany(list => list);
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

    public IGameSession CreateSession(string sessionId, DateTime expiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("Session id must be non-empty.", nameof(sessionId));

        var now = DateTime.UtcNow;
        if (expiresAtUtc <= now)
            throw new ArgumentException("Expiry must be in the future (UTC).", nameof(expiresAtUtc));

        while (true)
        {
            if (_sessions.TryGetValue(sessionId, out var existing))
            {
                if (existing.Expired())
                {
                    // Remove stale session and loop to add fresh
                    if (_sessions.TryRemove(sessionId, out _))
                        continue;
                }

                // Non-expired: just renew expiry and reuse contexts
                existing.Renew(expiresAtUtc);
                return existing;
            }

            var created = new GameSession(sessionId, expiresAtUtc);
            if (_sessions.TryAdd(sessionId, created))
                return created;

            // some concurrent writer won the race, loop and inspect
        }
    }

    public IGameSession? GetSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return null;

        if (!_sessions.TryGetValue(sessionId, out var session))
            return null;

        if (session.Expired())
        {
            // auto-clean expired session on access
            ClearSession(sessionId);
            return null;
        }

        return session;
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

    public IGameSession? MigrateSession(string fromSessionId, string toSessionId, DateTime newExpiresAtUtc)
    {
        if (string.IsNullOrWhiteSpace(fromSessionId))
            throw new ArgumentException("Source session id must be non-empty.", nameof(fromSessionId));
        if (string.IsNullOrWhiteSpace(toSessionId))
            throw new ArgumentException("Target session id must be non-empty.", nameof(toSessionId));

        // Expiry must be strictly in the future (UTC); otherwise do nothing.
        if (newExpiresAtUtc <= DateTime.UtcNow)
            return null;

        // Same id: just renew / create and return that session.
        if (string.Equals(fromSessionId, toSessionId, StringComparison.Ordinal))
        {
            var same = GetSession(fromSessionId) as GameSession;
            if (same != null)
            {
                same.Renew(newExpiresAtUtc);
                return same;
            }

            return CreateSession(fromSessionId, newExpiresAtUtc);
        }

        // Determine source (only if non-expired)
        GameSession? fromSession = null;
        if (_sessions.TryGetValue(fromSessionId, out var src))
        {
            if (src.Expired())
            {
                ClearSession(fromSessionId);
            }
            else
            {
                fromSession = src;
            }
        }

        // Ensure / get target session (non-expired, with new expiry)
        GameSession toSession;
        var existingTarget = GetSession(toSessionId) as GameSession;
        if (existingTarget == null)
        {
            toSession = (GameSession)CreateSession(toSessionId, newExpiresAtUtc);
        }
        else
        {
            existingTarget.Renew(newExpiresAtUtc);
            toSession = existingTarget;
        }

        // Move contexts if we have a valid source
        if (fromSession != null)
        {
            fromSession.MoveAllContextsTo(toSession);
            _sessions.TryRemove(fromSessionId, out _);
        }

        return toSession;
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
            // Auto-remove expired sessions, keep live ones.
            foreach (var kvp in _sessions)
            {
                var id = kvp.Key;
                var session = kvp.Value;

                if (session.Expired())
                {
                    if (_sessions.TryRemove(id, out var removed))
                    {
                        removed.ClearAllContexts();
                    }
                }
            }

            await _socketManager.Cleanup();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error cleaning up connections and contexts.");
        }
    }

    public IEnumerable<T> FindAllContexts<T>() where T : class
    {
        var allSessions = _sessions.Values;
        return allSessions.SelectMany(s => s.FindAllContexts<T>());
    }

    public IEnumerable<object> FindAllContexsts(string sessionId)
    {
        var session = GetSession(sessionId);
        return session?.FindAllContexts() ?? Enumerable.Empty<object>();
    }
}
