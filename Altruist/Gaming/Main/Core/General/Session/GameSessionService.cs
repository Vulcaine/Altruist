using System.Collections.Concurrent;

using Altruist;

using Microsoft.Extensions.Logging;

/// <summary>Packets produced by a session operation: some for the client, some broadcast to socket rooms.</summary>
public sealed class GameSessionResult
{
    /// <summary>Packets for the requesting client.</summary>
    public List<IPacketBase> ClientPackets { get; } = new();
    /// <summary>Packets for every connection of a socket room.</summary>
    public List<RoomBroadcast> RoomBroadcasts { get; } = new();

    /// <summary>A new empty result (a fresh instance on every call).</summary>
    public static GameSessionResult Empty => new GameSessionResult();
}

/// <summary>
/// A server-side session: a TTL plus typed context objects stored under inner ids (one value per
/// (id, type)). Thread-safe. Get one from <see cref="IGameSessionService"/>; <see cref="Altruist.Gaming.AltruistGameSessionPortal"/>
/// creates one per connection keyed by the client id.
/// <para>
/// When to use: ad-hoc per-player or per-connection state that should expire on its own (pending
/// requests, selected character, temporary flags). Long-lived game state belongs in your world objects or
/// persistence; match state belongs in the room's simulation (<c>RoomHost</c>).
/// </para>
/// <example>
/// <code>
/// var session = sessions.CreateSession(accountId, DateTime.UtcNow.AddMinutes(30));
/// session.SetContext("party", new PartyInvite(fromId));
/// var invite = session.GetContext&lt;PartyInvite&gt;("party");
/// </code>
/// </example>
/// </summary>
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
    /// <exception cref="ArgumentException"><paramref name="newExpiresAtUtc"/> is not in the future (UTC).</exception>
    void Renew(DateTime newExpiresAtUtc);

    /// <summary>
    /// Whether this session is expired at the time of the call.
    /// </summary>
    bool Expired();

    /// <summary>
    /// Store a context object inside this session, bound to a context id and type.
    /// Only one instance per (context id, type) is kept (last write wins).
    /// </summary>
    void SetContext<T>(string id, T value);

    /// <summary>
    /// Get the most recently stored context of type T for the given context id (if any).
    /// </summary>
    T? GetContext<T>(string id);

    /// <summary>Every stored context assignable to <typeparamref name="T"/>, across all inner ids.</summary>
    IEnumerable<T> FindAllContexts<T>();

    /// <summary>Every stored context that is an instance of one of <paramref name="types"/> (no types: all of them), as a snapshot list.</summary>
    IEnumerable<object> FindContexts(params Type[] types);

    /// <summary>Every stored context, across all inner ids.</summary>
    IEnumerable<object> FindAllContexts();

    /// <summary>
    /// Remove a context of type T for the given context id (if any).
    /// </summary>
    void RemoveContext<T>(string id);

    /// <summary>
    /// Remove all contexts for this session (for all inner ids).
    /// </summary>
    void ClearAllContexts();
}

/// <summary>
/// Registry of <see cref="IGameSession"/>s by a global id (account id, user id or client id), plus the
/// default logic of the classic handshake / join / leave packets used by
/// <see cref="Altruist.Gaming.AltruistGameSessionPortal"/>. The default implementation is
/// <see cref="GameSessionService"/> (singleton <c>[Service]</c>); replace or subclass it to change the
/// join/leave rules. Thread-safe.
/// </summary>
public interface IGameSessionService
{
    // -------------------------
    // Session object API
    // -------------------------

    /// <summary>
    /// Create or get a session for the given global session id (e.g. accountId),
    /// with a specific expiry time (UTC).
    /// If a non-expired session already exists, its expiry is renewed to the given time.
    /// If an expired session exists, it is cleared and replaced with a new one.
    /// </summary>
    /// <param name="sessionId">The global id (non-empty).</param>
    /// <param name="expiresAtUtc">Expiry, strictly in the future (UTC).</param>
    /// <returns>The live session.</returns>
    /// <exception cref="ArgumentException">The id is blank or the expiry is not in the future.</exception>
    /// <example>
    /// <code>
    /// var session = _gameSessionService.CreateSession(globalSessionId, DateTime.UtcNow.AddMinutes(30));
    /// session.SetContext(innerId, value);
    /// </code>
    /// </example>
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
    /// moves all contexts, and removes the source session (use it when a guest/connection id becomes
    /// an account id after login). Returns null, changing nothing, when the expiry is not in the future.
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

    /// <summary>Every context assignable to <typeparamref name="T"/> across all sessions (expired ones included until cleaned up).</summary>
    IEnumerable<T> FindAllContexts<T>();

    /// <summary>Every context assignable to <typeparamref name="T"/> in one live session (empty when it does not exist or expired).</summary>
    IEnumerable<T> FindAllContexts<T>(string id);

    /// <summary>Every context of one live session (empty when it does not exist or expired). Note the misspelled name.</summary>
    IEnumerable<object> FindAllContexsts(string sessionId);

    /// <summary>Contexts of one live session that are instances of one of <paramref name="types"/> (no types: all).</summary>
    IEnumerable<object> FindContexts(string id, params Type[] types);

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

    public void SetContext<T>(string id, T value)
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

        return;
    }

    public T? GetContext<T>(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return default;

        lock (_lock)
        {
            if (_contexts.TryGetValue(id, out var list))
            {
                for (int i = list.Count - 1; i >= 0; --i)
                {
                    if (list[i] is T t)
                    {
                        return t;
                    }
                }
            }
        }

        return default;
    }

    public void RemoveContext<T>(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            return;

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

        return;
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

    public IEnumerable<T> FindAllContexts<T>()
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

    public IEnumerable<object> FindContexts(params Type[] types)
    {
        // If no types specified, behave like "all"
        if (types == null || types.Length == 0)
        {
            lock (_lock)
            {
                return _contexts.Values
                    .SelectMany(list => list)
                    .ToList();
            }
        }

        lock (_lock)
        {
            var typeSet = new HashSet<Type>(types);

            return _contexts.Values
                .SelectMany(list => list)
                .Where(obj =>
                    obj != null &&
                    typeSet.Any(t => t.IsInstanceOfType(obj)))
                .ToList();
        }
    }

}

/// <summary>
/// Default <see cref="IGameSessionService"/>: in-memory sessions in a concurrent dictionary (per process,
/// not shared across servers), and default handshake / join / leave logic on the socket rooms of
/// <see cref="ISocketManager"/>. Registered as <c>[Service(typeof(IGameSessionService))]</c>; the
/// packet methods are virtual, so subclass it (and register the subclass) to change them.
/// </summary>
[Service(typeof(IGameSessionService))]
public class GameSessionService : IGameSessionService
{
    private readonly ISocketManager _socketManager;
    private readonly ILogger _logger;

    // All sessions are keyed by a global session id (e.g. accountId, userId, etc.)
    private readonly ConcurrentDictionary<string, GameSession> _sessions =
        new(StringComparer.Ordinal);

    /// <summary>Created by DI.</summary>
    /// <param name="socketManager">Connection and socket-room registry.</param>
    /// <param name="loggerFactory">Logging.</param>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
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

    /// <summary>Answers a handshake with the list of every socket room (<see cref="HandshakeResponsePacket"/>, accepted).</summary>
    /// <param name="message">The request.</param>
    /// <param name="clientId">The client.</param>
    /// <returns>A success result carrying the room list.</returns>
    public virtual async Task<IResultPacket> HandshakeAsync(
        HandshakeRequestPacket message,
        string clientId)
    {
        var rooms = await _socketManager.GetAllRoomsAsync();

        var responsePacket = new HandshakeResponsePacket(rooms.Values.ToArray());

        return ResultPacket.Success(TransportCode.Accepted, responsePacket);
    }

    /// <summary>
    /// Removes the client from its socket room (deleting the room when it becomes empty), clears the session
    /// keyed by <paramref name="clientId"/>, and returns a <see cref="LeaveGamePacket"/> broadcast for that room
    /// (null when the client was in no room).
    /// </summary>
    /// <param name="message">The request.</param>
    /// <param name="clientId">The client.</param>
    /// <returns>The broadcast, or null.</returns>
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

    /// <summary>
    /// Validates a join: a name is required; the given room (or, without a room id, the first socket room
    /// with free capacity) must exist and must not already contain the client. It does not add the client to
    /// the room: do that in an override or in <see cref="Altruist.Gaming.AltruistGameSessionPortal"/>'s join hook.
    /// </summary>
    /// <param name="message">The request.</param>
    /// <param name="clientId">The client.</param>
    /// <returns>A failed result with the reason, or a success result with a message.</returns>
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

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public IEnumerable<T> FindAllContexts<T>()
    {
        var allSessions = _sessions.Values;
        return allSessions.SelectMany(s => s.FindAllContexts<T>());
    }

    /// <inheritdoc/>
    public IEnumerable<object> FindAllContexsts(string sessionId)
    {
        var session = GetSession(sessionId);
        return session?.FindAllContexts() ?? Enumerable.Empty<object>();
    }

    /// <inheritdoc/>
    public IEnumerable<object> FindContexts(string id, params Type[] types)
    {
        var session = GetSession(id);
        return session?.FindContexts(types) ?? Enumerable.Empty<object>();
    }

    /// <inheritdoc/>
    public IEnumerable<T> FindAllContexts<T>(string id)
    {
        var session = GetSession(id);
        return session?.FindAllContexts<T>() ?? Enumerable.Empty<T>();
    }
}
