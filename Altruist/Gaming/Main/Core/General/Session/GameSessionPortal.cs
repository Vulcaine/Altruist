using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

public abstract class AltruistGameSessionPortal : Portal, OnConnectedAsync, OnDisconnectedAsync
{
    protected readonly IGameSessionService _gameSessionService;
    protected readonly IAltruistRouter _router;
    private readonly int _sessionTtlMinutes;

    /// <summary>
    /// Snapshot of every <see cref="IClientSessionCleanup"/> registered in DI.
    /// Each one's <see cref="IClientSessionCleanup.Cleanup"/> fires on every client
    /// disconnect — see <see cref="OnDisconnectedAsync"/>.
    /// </summary>
    private readonly IClientSessionCleanup[] _sessionServices;
    private readonly ILogger _logger;

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

    [Gate(IngressEP.JoinGame)]
    public async Task JoinGameAsync(
        JoinGamePacket message,
        string clientId)
    {
        var serviceResult = await _gameSessionService.JoinGameAsync(message, clientId);

        var finalResult = await OnJoinGameReceived(message, clientId, serviceResult);

        await PublishResultAsync(clientId, finalResult);
    }

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

    public async Task OnConnectedAsync(string clientId, ConnectionManager connectionManager, AltruistConnection connection)
    {
        var session = _gameSessionService.CreateSession(clientId, DateTime.UtcNow.AddMinutes(_sessionTtlMinutes));
        await OnSessionConnectedAsync(clientId, session, connectionManager, connection);
    }

    public async Task Cleanup()
    {
        await _gameSessionService.Cleanup();
    }

    // -----------------------------------
    // Hooks: default returns input result
    // -----------------------------------

    protected virtual Task<IResultPacket> OnHandshakeReceived(
        HandshakeRequestPacket message,
        string clientId,
        IResultPacket result)
        => Task.FromResult(result);

    protected virtual Task<RoomBroadcast?> OnExitGameReceived(
        LeaveGamePacket message,
        string clientId,
        RoomBroadcast? result)
        => Task.FromResult(result);

    protected virtual Task<IResultPacket> OnJoinGameReceived(
        JoinGamePacket message,
        string clientId,
        IResultPacket result)
        => Task.FromResult(result);

    protected virtual Task OnSessionConnectedAsync(
        string clientId,
        IGameSession session,
        ConnectionManager connectionManager,
        AltruistConnection connection)
        => Task.CompletedTask;

    protected virtual Task OnSessionDisconnectingAsync(
        string clientId,
        IGameSession? session,
        Exception? exception)
        => Task.CompletedTask;

    protected virtual Task OnSessionDisconnectedAsync(
        string clientId,
        Exception? exception)
        => Task.CompletedTask;

    // -----------------------------------
    // Publishing logic (framework owned)
    // -----------------------------------

    // For handshake / join (ResultPacket → client)
    protected virtual async Task PublishResultAsync(string clientId, IResultPacket result)
    {
        if (result is not IResultPacketWithPayload payloadPacket)
            return;

        if (payloadPacket.Payload is null)
            return;

        await _router.Client.SendAsync(clientId, payloadPacket.Payload);
    }

    // For exit game (RoomBroadcast → room)
    protected virtual async Task PublishResultAsync(RoomBroadcast? result)
    {
        if (result is null)
            return;

        await _router.Room.SendAsync(result.RoomId, result.Packet);
    }
}
