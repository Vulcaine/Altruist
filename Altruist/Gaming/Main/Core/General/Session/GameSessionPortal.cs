using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

/// <summary>
/// Base portal for the classic connect / handshake / join / leave flow: creates an <see cref="IGameSession"/>
/// (keyed by the client id, TTL <c>altruist:game:session:ttl-minutes</c>, default 60) when a client connects,
/// answers the framework's <see cref="IngressEP.Handshake"/>, <see cref="IngressEP.JoinGame"/> and
/// <see cref="IngressEP.LeaveGame"/> packets through <see cref="IGameSessionService"/>, and on disconnect
/// clears the session and runs every <see cref="IClientSessionCleanup"/>.
/// <para>
/// When to use: derive from it (with <c>[Portal("/your-path")]</c>) when clients use the built-in
/// handshake/join/leave packets and the socket-level rooms of <see cref="ISocketManager"/> (broadcast
/// groups), typically with persistent worlds. Override the <c>On...Received</c> hooks to change the
/// answers and the <c>OnSession...</c> hooks for your own per-connection setup and teardown. For
/// authoritative match rooms with seats, bots and reconnects use <c>RoomHost</c> (Altruist.Gaming.Rooms)
/// with your own portal instead; for plain per-client cleanup without this flow implement
/// <see cref="IClientSessionCleanup"/>.
/// </para>
/// <example>
/// <code>
/// [Portal("/game")]
/// public sealed class GamePortal : AltruistGameSessionPortal
/// {
///     public GamePortal(IGameSessionService sessions, IAltruistRouter router,
///         IEnumerable&lt;IClientSessionCleanup&gt; cleanups, ILoggerFactory logs)
///         : base(sessions, router, cleanups, logs) { }
///
///     protected override Task OnSessionConnectedAsync(string clientId, IGameSession session,
///         ConnectionManager cm, AltruistConnection connection)
///     {
///         session.SetContext(clientId, new PlayerState());
///         return Task.CompletedTask;
///     }
/// }
/// </code>
/// </example>
/// </summary>
public abstract class AltruistGameSessionPortal : Portal, OnConnectedAsync, OnDisconnectedAsync
{
    /// <summary>The session service (sessions, contexts, the default handshake/join/leave logic).</summary>
    protected readonly IGameSessionService _gameSessionService;
    /// <summary>The router the results are published through.</summary>
    protected readonly IAltruistRouter _router;
    private readonly int _sessionTtlMinutes;

    /// <summary>
    /// Snapshot of every <see cref="IClientSessionCleanup"/> registered in DI.
    /// Each one's <see cref="IClientSessionCleanup.Cleanup"/> fires on every client
    /// disconnect — see <see cref="OnDisconnectedAsync"/>.
    /// </summary>
    private readonly IClientSessionCleanup[] _sessionServices;
    private readonly ILogger _logger;

    /// <summary>Called by the derived portal's DI constructor.</summary>
    /// <param name="gameSessionService">Session store and default packet logic.</param>
    /// <param name="router">Where results are sent.</param>
    /// <param name="sessionServices">Every registered <see cref="IClientSessionCleanup"/> (registered automatically by <see cref="ClientSessionCleanupConfiguration"/>).</param>
    /// <param name="loggerFactory">Logging.</param>
    /// <param name="sessionTtlMinutes">Session lifetime from connect, in minutes (config <c>altruist:game:session:ttl-minutes</c>, default 60; at least 1).</param>
    protected AltruistGameSessionPortal(
        IGameSessionService gameSessionService,
        IAltruistRouter router,
        IEnumerable<IClientSessionCleanup> sessionServices,
        ILoggerFactory loggerFactory,
        [AppConfigValue("altruist:game:session:ttl-minutes", "60")] int sessionTtlMinutes = 60)
    {
        _gameSessionService = gameSessionService;
        _router = router;
        _sessionTtlMinutes = Math.Max(1, sessionTtlMinutes);
        _sessionServices = (sessionServices ?? Enumerable.Empty<IClientSessionCleanup>()).ToArray();
        _logger = loggerFactory.CreateLogger(GetType());
    }

    /// <summary>Handles <see cref="IngressEP.Handshake"/>: the service's answer (default: the list of socket rooms), adjusted by <see cref="OnHandshakeReceived"/>, sent to the client.</summary>
    /// <param name="message">The handshake request.</param>
    /// <param name="clientId">The sending client.</param>
    [Gate(IngressEP.Handshake)]
    public async Task HandshakeAsync(
        HandshakeRequestPacket message,
        string clientId)
    {
        // 1) Core logic from service
        var serviceResult = await _gameSessionService.HandshakeAsync(message, clientId);

        // 2) Let user adjust/extend the result
        var finalResult = await OnHandshakeReceived(message, clientId, serviceResult);

        // 3) Publish
        await PublishResultAsync(clientId, finalResult);
    }

    /// <summary>Handles <see cref="IngressEP.LeaveGame"/>: the service removes the client from its socket room, and the resulting broadcast (after <see cref="OnExitGameReceived"/>) goes to that room.</summary>
    /// <param name="message">The leave request.</param>
    /// <param name="clientId">The sending client.</param>
    [Gate(IngressEP.LeaveGame)]
    public async Task ExitGameAsync(
        LeaveGamePacket message,
        string clientId)
    {
        // Service returns optional room broadcast
        var serviceResult = await _gameSessionService.ExitGameAsync(message, clientId);

        var finalResult = await OnExitGameReceived(message, clientId, serviceResult);

        await PublishResultAsync(finalResult);
    }

    /// <summary>Handles <see cref="IngressEP.JoinGame"/>: the service validates the join, <see cref="OnJoinGameReceived"/> may change the result, and its payload is sent to the client.</summary>
    /// <param name="message">The join request (player name, optional room id).</param>
    /// <param name="clientId">The sending client.</param>
    [Gate(IngressEP.JoinGame)]
    public async Task JoinGameAsync(
        JoinGamePacket message,
        string clientId)
    {
        var serviceResult = await _gameSessionService.JoinGameAsync(message, clientId);

        var finalResult = await OnJoinGameReceived(message, clientId, serviceResult);

        await PublishResultAsync(clientId, finalResult);
    }

    /// <summary>
    /// Disconnect hook: <see cref="OnSessionDisconnectingAsync"/> (session still readable), then the session is
    /// cleared, every <see cref="IClientSessionCleanup"/> runs (a failing one is logged, the rest still run), then
    /// <see cref="OnSessionDisconnectedAsync"/>.
    /// </summary>
    /// <param name="clientId">The disconnected client.</param>
    /// <param name="exception">The error that closed it, or null.</param>
    public async Task OnDisconnectedAsync(string clientId, Exception? exception)
    {
        var session = _gameSessionService.GetSession(clientId);
        await OnSessionDisconnectingAsync(clientId, session, exception);
        _gameSessionService.ClearSession(clientId);

        // Fan out to every [Service] that opted into per-client cleanup by
        // implementing IClientSessionCleanup. One bad handler doesn't block the
        // rest — log and continue. Runs after ClearSession so a service that
        // also overrides OnSessionDisconnectingAsync above has had its chance
        // to read GameSession state before the framework cleanup tears down.
        for (int i = 0; i < _sessionServices.Length; i++)
        {
            try
            {
                await _sessionServices[i].Cleanup(clientId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "{Type}.Cleanup failed for client {ClientId}",
                    _sessionServices[i].GetType().Name, clientId);
            }
        }

        await OnSessionDisconnectedAsync(clientId, exception);
    }

    /// <summary>Connect hook: creates (or renews) the client's session with the configured TTL, then calls <see cref="OnSessionConnectedAsync"/>.</summary>
    /// <param name="clientId">The connected client.</param>
    /// <param name="connectionManager">The connection manager handling it.</param>
    /// <param name="connection">The registered connection.</param>
    public async Task OnConnectedAsync(string clientId, ConnectionManager connectionManager, AltruistConnection connection)
    {
        var session = _gameSessionService.CreateSession(clientId, DateTime.UtcNow.AddMinutes(_sessionTtlMinutes));
        await OnSessionConnectedAsync(clientId, session, connectionManager, connection);
    }

    /// <summary>Removes expired sessions and stale socket state (<see cref="IGameSessionService.Cleanup"/>).</summary>
    public async Task Cleanup()
    {
        await _gameSessionService.Cleanup();
    }

    // -----------------------------------
    // Hooks: default returns input result
    // -----------------------------------

    /// <summary>Override to adjust the handshake answer; default returns <paramref name="result"/> unchanged.</summary>
    /// <param name="message">The request.</param>
    /// <param name="clientId">The client.</param>
    /// <param name="result">The service's answer.</param>
    /// <returns>The answer to send (only an <see cref="IResultPacketWithPayload"/> payload is sent).</returns>
    protected virtual Task<IResultPacket> OnHandshakeReceived(
        HandshakeRequestPacket message,
        string clientId,
        IResultPacket result)
        => Task.FromResult(result);

    /// <summary>Override to adjust the leave broadcast; default returns <paramref name="result"/> unchanged (null broadcasts nothing).</summary>
    /// <param name="message">The request.</param>
    /// <param name="clientId">The client.</param>
    /// <param name="result">The service's broadcast, or null.</param>
    /// <returns>The broadcast to send, or null.</returns>
    protected virtual Task<RoomBroadcast?> OnExitGameReceived(
        LeaveGamePacket message,
        string clientId,
        RoomBroadcast? result)
        => Task.FromResult(result);

    /// <summary>Override to adjust the join answer (put the player into your world here); default returns <paramref name="result"/> unchanged.</summary>
    /// <param name="message">The request.</param>
    /// <param name="clientId">The client.</param>
    /// <param name="result">The service's answer.</param>
    /// <returns>The answer to send (only an <see cref="IResultPacketWithPayload"/> payload is sent).</returns>
    protected virtual Task<IResultPacket> OnJoinGameReceived(
        JoinGamePacket message,
        string clientId,
        IResultPacket result)
        => Task.FromResult(result);

    /// <summary>Override for per-connection setup; the session was just created. Default does nothing.</summary>
    /// <param name="clientId">The client.</param>
    /// <param name="session">Its new session.</param>
    /// <param name="connectionManager">The connection manager handling it.</param>
    /// <param name="connection">The connection.</param>
    protected virtual Task OnSessionConnectedAsync(
        string clientId,
        IGameSession session,
        ConnectionManager connectionManager,
        AltruistConnection connection)
        => Task.CompletedTask;

    /// <summary>Override to read session state before it is cleared on disconnect (save progress, notify others). Default does nothing.</summary>
    /// <param name="clientId">The client.</param>
    /// <param name="session">Its session, or null when it expired.</param>
    /// <param name="exception">The error that closed it, or null.</param>
    protected virtual Task OnSessionDisconnectingAsync(
        string clientId,
        IGameSession? session,
        Exception? exception)
        => Task.CompletedTask;

    /// <summary>Override for work after the session and every <see cref="IClientSessionCleanup"/> ran. Default does nothing.</summary>
    /// <param name="clientId">The client.</param>
    /// <param name="exception">The error that closed it, or null.</param>
    protected virtual Task OnSessionDisconnectedAsync(
        string clientId,
        Exception? exception)
        => Task.CompletedTask;

    // -----------------------------------
    // Publishing logic (framework owned)
    // -----------------------------------

    // For handshake / join (ResultPacket → client)
    /// <summary>Sends the payload of a result to the client (results without payload send nothing).</summary>
    /// <param name="clientId">The client.</param>
    /// <param name="result">The result.</param>
    protected virtual async Task PublishResultAsync(string clientId, IResultPacket result)
    {
        if (result is not IResultPacketWithPayload payloadPacket)
            return;

        if (payloadPacket.Payload is null)
            return;

        await _router.Client.SendAsync(clientId, payloadPacket.Payload);
    }

    // For exit game (RoomBroadcast → room)
    /// <summary>Sends a broadcast to its socket room (null sends nothing).</summary>
    /// <param name="result">The broadcast, or null.</param>
    protected virtual async Task PublishResultAsync(RoomBroadcast? result)
    {
        if (result is null)
            return;

        await _router.Room.SendAsync(result.RoomId, result.Packet);
    }
}
