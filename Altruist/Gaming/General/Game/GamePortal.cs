using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;


public abstract class AltruistGamePortal<TPlayerEntity> : Portal where TPlayerEntity : PlayerEntity, new()
{
    protected readonly IPlayerService<TPlayerEntity> _playerService;

    protected AltruistGamePortal(IPortalContext context, ILoggerFactory loggerFactory) : base(context, loggerFactory)
    {
        _playerService = context.GetPlayerService<TPlayerEntity>();
    }

    [Gate(IngressEP.HANDSHAKE)]
    public async virtual Task HandshakeAsync(HandshakePacket message, string clientId)
    {
        var rooms = await GetAllRoomsAsync();
        var responsePacket = new HandshakePacket("server", rooms.Values.ToArray(), clientId);
        await Router.Client.SendAsync(clientId, responsePacket);
    }

    [Gate(IngressEP.LEAVE_GAME)]
    public async virtual Task ExitGameAsync(LeaveGamePacket message, string clientId)
    {
        var player = await _playerService.GetPlayerAsync(clientId);

        if (player != null)
        {
            await _playerService.DisconnectAsync(clientId);
            var room = await FindRoomForClientAsync(clientId);
            var msg = $"Player {player.Name} left the game";
            if (room != null)
            {
                await Router.Room.SendAsync(room.Id, PacketHelper.Success(msg, clientId));
            }
            else
            {
                await Router.Client.SendAsync(clientId, PacketHelper.Success(msg, clientId));
            }

        }
    }

    [Gate(IngressEP.JOIN_GAME)]
    public async virtual Task JoinGameAsync(JoinGamePacket message, string clientId)
    {
        if (string.IsNullOrEmpty(message.Name))
        {
            await Router.Client.SendAsync(clientId, PacketHelper.JoinFailed("Username is required!", clientId));
            return;
        }

        RoomPacket? room;
        if (!string.IsNullOrEmpty(message.RoomId))
        {
            room = await GetRoomAsync(message.RoomId);

            if (room == null)
            {
                var joinFailedMsg = $"Join failed. No such room: {message.RoomId}";
                await Router.Client.SendAsync(clientId, PacketHelper.JoinFailed(joinFailedMsg, clientId));
                return;
            }
        }
        else
        {
            room = await FindAvailableRoomAsync();
        }

        if (room == null)
        {
            var msg = $"Join failed: No available rooms";
            await Router.Client.SendAsync(clientId, PacketHelper.JoinFailed(msg, clientId));
            Logger.LogWarning(msg);
        }
        else if (room.Has(clientId))
        {
            var msg = $"Join failed: {clientId} is already in the game";
            await Router.Client.SendAsync(clientId, PacketHelper.JoinFailed(msg, clientId));
            Logger.LogWarning(msg);
        }
        else
        {
            var msg = $"Player {message.Name} joined room {room}";
            var player = await _playerService.ConnectById(room.Id, clientId, message.Name, message.Position);
            if (player == null)
            {
                var joinFailedMsg = $"Join failed. No such room: {message.RoomId}";
                await Router.Client.SendAsync(clientId, PacketHelper.JoinFailed(joinFailedMsg, clientId));
            }
            else
            {
                await Router.Client.SendAsync(clientId, PacketHelper.Success(msg, clientId));
                await Router.Synchronize.SendAsync(player);

            }

            Logger.LogInformation(msg);
        }
    }

    [Cycle(cron: CronPresets.Hourly)]
    public override async Task Cleanup()
    {
        await base.Cleanup();
        await _playerService.Cleanup();
    }

}

