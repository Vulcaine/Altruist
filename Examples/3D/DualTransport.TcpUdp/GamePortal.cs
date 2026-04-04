using System.Collections.Concurrent;
using Altruist;
using Microsoft.Extensions.Logging;

namespace DualTransport;

/// <summary>
/// TCP portal — handles reliable messages: login, chat.
/// Route: /game (TCP transport)
/// </summary>
[Portal("/game")]
public class TcpGamePortal : Portal, OnConnectedAsync, OnDisconnectedAsync
{
    private static readonly ConcurrentDictionary<string, string> _names = new();

    private readonly IAltruistRouter _router;
    private readonly ILogger _logger;

    public TcpGamePortal(IAltruistRouter router, ILoggerFactory loggerFactory)
    {
        _router = router;
        _logger = loggerFactory.CreateLogger<TcpGamePortal>();
    }

    public Task OnConnectedAsync(string clientId, ConnectionManager manager, AltruistConnection connection)
    {
        _logger.LogInformation("[TCP] Client connected: {ClientId}", clientId);
        return Task.CompletedTask;
    }

    public Task OnDisconnectedAsync(string clientId, Exception? exception)
    {
        _names.TryRemove(clientId, out _);
        _logger.LogInformation("[TCP] Client disconnected: {ClientId}", clientId);
        return Task.CompletedTask;
    }

    [Gate("login")]
    public async Task OnLogin(CLogin packet, string clientId)
    {
        _names[clientId] = packet.Name;
        await _router.Client.SendAsync(clientId, new SLoginOk { PlayerId = clientId });
        _logger.LogInformation("[TCP] {Name} logged in", packet.Name);
    }

    [Gate("chat")]
    public async Task OnChat(CChat packet, string clientId)
    {
        if (!_names.TryGetValue(clientId, out var sender)) return;
        await _router.Broadcast.SendAsync(new SChatBroadcast
        {
            Sender = sender,
            Message = packet.Message,
        });
    }

    public static string? GetName(string clientId)
        => _names.TryGetValue(clientId, out var n) ? n : null;
}

/// <summary>
/// UDP portal — handles fast unreliable messages: position updates.
/// Route: /game (UDP transport on separate port)
/// In a real setup, UDP packets bypass the TCP connection manager.
/// </summary>
[Portal("/game")]
public class UdpMovementPortal : Portal
{
    private readonly IAltruistRouter _router;

    public UdpMovementPortal(IAltruistRouter router) => _router = router;

    [Gate("position")]
    public async Task OnPosition(CPositionUpdate packet, string clientId)
    {
        // Broadcast position to all other players (low latency, no reliability needed)
        await _router.Broadcast.SendAsync(new SPositionBroadcast
        {
            PlayerId = clientId,
            X = packet.X, Y = packet.Y, Z = packet.Z,
            Yaw = packet.Yaw,
        });
    }
}
