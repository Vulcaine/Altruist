using Altruist;
using Altruist.Gaming;
using Microsoft.Extensions.Logging;

public interface IGameSessionService
{
    Task Cleanup();
    Task ExitGameAsync(LeaveGamePacket message, string clientId);
    Task JoinGameAsync(JoinGamePacket message, string clientId);
    Task HandshakeAsync(HandshakePacket message, string clientId);
}

[Service(typeof(IGameSessionService))]
public class GameSessionService : IGameSessionService
{
    private readonly IPlayerService _playerService;
    private readonly ISocketManager _socketManager;
    private readonly IAltruistRouter _router;

    private readonly ILogger _logger;

    public GameSessionService(IPlayerService playerService, ISocketManager socketManager, IAltruistRouter router, ILoggerFactory loggerFactory)
    {
        _playerService = playerService;
        _socketManager = socketManager;
        _router = router;
        _logger = loggerFactory.CreateLogger(GetType());
    }

    public async virtual Task HandshakeAsync(HandshakePacket message, string clientId)
    {
        var rooms = await _socketManager.GetAllRoomsAsync();
        // TODO: fill out user token
        var responsePacket = new HandshakePacket("server", rooms.Values.ToArray(), clientId);
        await _router.Client.SendAsync(clientId, responsePacket);
    }

    public async virtual Task ExitGameAsync(LeaveGamePacket message, string clientId)
    {
        var player = await _playerService.GetPlayerAsync(clientId);

        if (player != null)
        {
            await _playerService.DisconnectAsync(clientId);
            var room = await _socketManager.FindRoomForClientAsync(clientId);
            var msg = $"Player {player.Name} left the game";

            _ = _router.Client.SendAsync(clientId, PacketHelper.Success(msg, clientId, message.Type));
            if (room != null)
            {
                var broadcastPacket = new LeaveGamePacket("server", clientId);
                room = room.RemoveConnection(clientId);
                _ = _socketManager.SaveRoomAsync(room);
                _ = _router.Room.SendAsync(room.Id, broadcastPacket);
                if (room.Empty())
                {
                    await _socketManager.DeleteRoomAsync(room.Id);
                }
            }
        }
    }

    public async virtual Task JoinGameAsync(JoinGamePacket message, string clientId)
    {
        if (string.IsNullOrEmpty(message.Name))
        {
            await _router.Client.SendAsync(clientId, PacketHelper.Failed("Username is required!", clientId, message.Type));
            return;
        }

        RoomPacket? room;
        if (!string.IsNullOrEmpty(message.RoomId))
        {
            room = await _socketManager.GetRoomAsync(message.RoomId);

            if (room == null)
            {
                var joinFailedMsg = $"Join failed. No such room: {message.RoomId}";
                await _router.Client.SendAsync(clientId, PacketHelper.Failed(joinFailedMsg, clientId, message.Type));
                return;
            }
        }
        else
        {
            room = await _socketManager.FindAvailableRoomAsync();
        }

        if (room == null)
        {
            var msg = $"Join failed: No available rooms";
            await _router.Client.SendAsync(clientId, PacketHelper.Failed(msg, clientId, message.Type));
            _logger.LogWarning(msg);
        }
        else if (room.Has(clientId))
        {
            var msg = $"Join failed: {clientId} is already in the game";
            await _router.Client.SendAsync(clientId, PacketHelper.Failed(msg, clientId, message.Type));
            _logger.LogWarning(msg);
        }
        else
        {
            var msg = $"Player {message.Name} joined the room: {room.Id}.";
            var player = await _playerService.ConnectById(room.Id, clientId, message.Name, message.WorldIndex ?? 0, message.Position);
            if (player == null)
            {
                var joinFailedMsg = $"Join failed. No such room: {message.RoomId}";
                await _router.Client.SendAsync(clientId, PacketHelper.Failed(joinFailedMsg, clientId, message.Type));
            }
            else
            {
                await _router.Client.SendAsync(clientId, PacketHelper.Success(msg, clientId, message.Type));
                await _router.Synchronize.SendAsync(player, forceAllAsChanged: true);
            }

            _logger.LogInformation(msg);
        }
    }

    public async Task Cleanup()
    {
        try
        {
            await _socketManager.Cleanup();
        }
        catch (Exception ex)
        {
            _logger.LogError($"Error cleaning up connections: {ex}");
        }
        await _playerService.Cleanup();
    }
}