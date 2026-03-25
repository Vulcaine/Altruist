using System.Collections.Concurrent;
using Altruist;
using GameServer.Packets;
using Microsoft.Extensions.Logging;

namespace GameServer;

[Portal("/game")]
public class GamePortal : Portal, OnConnectedAsync, OnDisconnectedAsync
{
    private static readonly ConcurrentDictionary<string, string> _playerNames = new();

    private readonly IAltruistRouter _router;
    private readonly ILogger _logger;

    public GamePortal(
        IAltruistRouter router,
        ILoggerFactory loggerFactory)
    {
        _router = router;
        _logger = loggerFactory.CreateLogger<GamePortal>();
    }

    public Task OnConnectedAsync(string clientId, ConnectionManager manager, AltruistConnection connection)
    {
        _logger.LogInformation("TCP client connected: {ClientId} from {Address}", clientId, connection.RemoteAddress);
        return Task.CompletedTask;
    }

    public async Task OnDisconnectedAsync(string clientId, Exception? exception)
    {
        if (_playerNames.TryRemove(clientId, out var name))
        {
            _logger.LogInformation("Player {Name} disconnected", name);

            await _router.Broadcast.SendAsync(new SEntityDespawn
            {
                EntityId = clientId,
            });
        }
    }

    [Gate("login")]
    public async Task OnLogin(CLogin packet, string clientId)
    {
        if (string.IsNullOrWhiteSpace(packet.Username))
        {
            await _router.Client.SendAsync(clientId, new SLoginFailure { Reason = "Username is required" });
            return;
        }

        if (_playerNames.Values.Contains(packet.Username))
        {
            await _router.Client.SendAsync(clientId, new SLoginFailure { Reason = "Username already taken" });
            return;
        }

        _playerNames[clientId] = packet.Username;

        // Send login success to the new player
        await _router.Client.SendAsync(clientId, new SLoginSuccess
        {
            PlayerId = clientId,
            SpawnX = Random.Shared.Next(0, 100),
            SpawnY = Random.Shared.Next(0, 100),
        });

        // Broadcast spawn to all players
        await _router.Broadcast.SendAsync(new SEntitySpawn
        {
            EntityId = clientId,
            Name = packet.Username,
            X = Random.Shared.Next(0, 100),
            Y = Random.Shared.Next(0, 100),
        });

        _logger.LogInformation("Player {Name} logged in", packet.Username);
    }

    [Gate("move")]
    public async Task OnMove(CMove packet, string clientId)
    {
        if (!_playerNames.ContainsKey(clientId))
            return;

        await _router.Broadcast.SendAsync(new SEntityUpdate
        {
            EntityId = clientId,
            X = packet.X,
            Y = packet.Y,
        });
    }

    [Gate("chat")]
    public async Task OnChat(CChat packet, string clientId)
    {
        if (!_playerNames.TryGetValue(clientId, out var sender))
            return;

        await _router.Broadcast.SendAsync(new SChatBroadcast
        {
            Sender = sender,
            Message = packet.Message,
        });
    }
}
