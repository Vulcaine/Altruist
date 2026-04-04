using System.Collections.Concurrent;
using Altruist;
using ChatServer.MessagePack.Packets;
using Microsoft.Extensions.Logging;

namespace ChatServer.MessagePack;

[Portal("/chat")]
public class ChatPortal : Portal, OnConnectedAsync, OnDisconnectedAsync
{
    private static readonly ConcurrentDictionary<string, string> _usernames = new();
    private static readonly ConcurrentDictionary<string, string> _clientRooms = new();

    private readonly IAltruistRouter _router;
    private readonly IConnectionManager _connectionManager;
    private readonly ILogger _logger;

    public ChatPortal(
        IAltruistRouter router,
        IConnectionManager connectionManager,
        ILoggerFactory loggerFactory)
    {
        _router = router;
        _connectionManager = connectionManager;
        _logger = loggerFactory.CreateLogger<ChatPortal>();
    }

    public async Task OnConnectedAsync(string clientId, ConnectionManager manager, AltruistConnection connection)
    {
        _logger.LogInformation("Client connected: {ClientId}", clientId);
        await _router.Client.SendAsync(clientId, new SSystemMessage
        {
            Message = "Connected. Send a join-room packet with your username."
        });
    }

    public async Task OnDisconnectedAsync(string clientId, Exception? exception)
    {
        if (_usernames.TryRemove(clientId, out var username) &&
            _clientRooms.TryRemove(clientId, out var room))
        {
            await _router.Room.SendAsync(room, new SUserLeft
            {
                Username = username,
                Room = room,
            });
            _logger.LogInformation("{Username} disconnected from room {Room}", username, room);
        }
    }

    [Gate("join-room")]
    public async Task OnJoinRoom(CJoinRoom packet, string clientId)
    {
        _usernames[clientId] = packet.Username;
        _clientRooms[clientId] = packet.Room;

        var room = await _connectionManager.GetRoomAsync(packet.Room)
                   ?? await _connectionManager.CreateRoomAsync(packet.Room);
        await _connectionManager.JoinRoomAsync(clientId, packet.Room);

        await _router.Room.SendAsync(packet.Room, new SUserJoined
        {
            Username = packet.Username,
            Room = packet.Room,
        });

        _logger.LogInformation("{Username} joined {Room}", packet.Username, packet.Room);
    }

    [Gate("chat")]
    public async Task OnChat(CChatMessage packet, string clientId)
    {
        if (!_usernames.TryGetValue(clientId, out var sender))
            return;
        if (!_clientRooms.TryGetValue(clientId, out var room))
            return;

        await _router.Room.SendAsync(room, new SChatMessage
        {
            Sender = sender,
            Message = packet.Message,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }
}
