using System.Collections.Concurrent;
using Altruist;
using ChatServer.Packets;
using Microsoft.Extensions.Logging;

namespace ChatServer;

[Portal("/chat")]
public class ChatPortal : Portal, OnConnectedAsync, OnDisconnectedAsync
{
    private static readonly ConcurrentDictionary<string, string> _usernames = new();
    private static readonly ConcurrentDictionary<string, string> _rooms = new();

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
        _logger.LogInformation("Client {ClientId} connected from {Address}", clientId, connection.RemoteAddress);
        await _router.Client.SendAsync(clientId, new SSystemMessage { Message = "Welcome! Send a join-room message to enter a chat room." });
    }

    public Task OnDisconnectedAsync(string clientId, Exception? exception)
    {
        _usernames.TryRemove(clientId, out var username);
        _rooms.TryRemove(clientId, out var room);

        if (username != null && room != null)
            _logger.LogInformation("{Username} left room {Room}", username, room);

        return Task.CompletedTask;
    }

    [Gate("join-room")]
    public async Task OnJoinRoom(CJoinRoom packet, string clientId)
    {
        _usernames[clientId] = packet.Username;
        _rooms[clientId] = packet.Room;

        var room = await _connectionManager.GetRoomAsync(packet.Room)
                   ?? await _connectionManager.CreateRoomAsync(packet.Room);
        await _connectionManager.JoinRoomAsync(clientId, packet.Room);

        _logger.LogInformation("{Username} joined room {Room}", packet.Username, packet.Room);

        await _router.Room.SendAsync(packet.Room, new SSystemMessage
        {
            Message = $"{packet.Username} joined the room."
        });

        var usersInRoom = _rooms
            .Where(kv => kv.Value == packet.Room)
            .Select(kv => _usernames.GetValueOrDefault(kv.Key, "Unknown"))
            .ToArray();

        await _router.Client.SendAsync(clientId, new SUserList
        {
            Users = usersInRoom,
            Room = packet.Room,
        });
    }

    [Gate("chat")]
    public async Task OnChat(CChatMessage packet, string clientId)
    {
        if (!_usernames.TryGetValue(clientId, out var sender))
            return;

        if (!_rooms.TryGetValue(clientId, out var room))
            return;

        _logger.LogDebug("[{Room}] {Sender}: {Message}", room, sender, packet.Message);

        await _router.Room.SendAsync(room, new SChatMessage
        {
            Sender = sender,
            Message = packet.Message,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });
    }
}
