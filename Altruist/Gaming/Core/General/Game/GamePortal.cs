using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

public abstract class AltruistGamePortal<TPlayerEntity> : Portal where TPlayerEntity : PlayerEntity, new()
{
    protected readonly GameWorldManager _world;

    protected AltruistGamePortal(IPortalContext context, GameWorldManager gameWorld, ILoggerFactory loggerFactory) : base(context, loggerFactory)
    {
        _world = gameWorld;
    }

    /// <summary>
    /// Broadcasts a packet to all clients in nearby partitions based on world position.
    /// </summary>
    /// <param name="x">The X coordinate in the world.</param>
    /// <param name="y">The Y coordinate in the world.</param>
    /// <param name="packet">The packet to be broadcasted.</param>
    protected void BroadcastToNearbyClients(int x, int y, IPacketBase packet)
    {
        var partitions = _world.FindPartitionsForPosition(x, y, 0);
        packet.Header = PacketHeaders.Broadcast;

        foreach (var partition in partitions)
        {
            var clients = partition.GetObjectsByType(WorldObjectTypeKeys.Client);
            foreach (var client in clients)
            {
                _ = Router.Client.SendAsync(client.InstanceId, packet);
            }
        }
    }

    /// <summary>
    /// Sends a packet to clients intelligently based on room size.
    /// If the room the sender belongs to has fewer players than the specified threshold,
    /// the packet is broadcast to the entire room. Otherwise, the packet is sent only
    /// to nearby clients using spatial partitioning based on the provided coordinates.
    /// </summary>
    /// <param name="senderClientId">The ID of the client sending the packet.</param>
    /// <param name="x">The X coordinate for spatial partition lookup.</param>
    /// <param name="y">The Y coordinate for spatial partition lookup.</param>
    /// <param name="packet">The packet to be sent to clients.</param>
    /// <param name="threshold">
    /// The maximum number of players in a room before switching to spatial broadcast.
    /// Defaults to 100.
    /// </param>
    /// <returns>A task representing the asynchronous operation.</returns>
    protected async Task SmartBroadcast(string senderClientId, int x, int y, IPacketBase packet, int threshold = 100)
    {
        var room = await FindRoomForClientAsync(senderClientId);
        if (room != null && room.PlayerCount < threshold)
        {
            _ = Router.Room.SendAsync(room.Id, packet);
        }
        else
        {
            BroadcastToNearbyClients(x, y, packet);
        }
    }
}

public abstract class AltruistGameSessionPortal<TPlayerEntity> : AltruistGamePortal<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    protected readonly IPlayerService<TPlayerEntity> _playerService;
    protected AltruistGameSessionPortal(IPortalContext context, GameWorldManager gameWorld, IPlayerService<TPlayerEntity> playerService, ILoggerFactory loggerFactory) : base(context, gameWorld, loggerFactory)
    {
        _playerService = playerService;
    }

    [Gate(IngressEP.Handshake)]
    public async virtual Task HandshakeAsync(HandshakePacket message, string clientId)
    {
        var rooms = await GetAllRoomsAsync();
        var responsePacket = new HandshakePacket("server", rooms.Values.ToArray(), clientId);
        await Router.Client.SendAsync(clientId, responsePacket);
    }

    [Gate(IngressEP.LeaveGame)]
    public async virtual Task ExitGameAsync(LeaveGamePacket message, string clientId)
    {
        var player = await _playerService.GetPlayerAsync(clientId);

        if (player != null)
        {
            await _playerService.DisconnectAsync(clientId);
            var room = await FindRoomForClientAsync(clientId);
            var msg = $"Player {player.Name} left the game";

            _ = Router.Client.SendAsync(clientId, PacketHelper.Success(msg, clientId, message.Type));
            if (room != null)
            {
                var broadcastPacket = new LeaveGamePacket("server", clientId);
                room = room.RemoveConnection(clientId);
                _ = SaveRoomAsync(room);
                _ = Router.Room.SendAsync(room.Id, broadcastPacket);
                if (room.Empty())
                {
                    await DeleteRoomAsync(room.Id);
                }
            }
        }
    }

    [Gate(IngressEP.JoinGame)]
    public async virtual Task JoinGameAsync(JoinGamePacket message, string clientId)
    {
        if (string.IsNullOrEmpty(message.Name))
        {
            await Router.Client.SendAsync(clientId, PacketHelper.Failed("Username is required!", message.Type, clientId));
            return;
        }

        RoomPacket? room;
        if (!string.IsNullOrEmpty(message.RoomId))
        {
            room = await GetRoomAsync(message.RoomId);

            if (room == null)
            {
                var joinFailedMsg = $"Join failed. No such room: {message.RoomId}";
                await Router.Client.SendAsync(clientId, PacketHelper.Failed(joinFailedMsg, message.Type, clientId));
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
            await Router.Client.SendAsync(clientId, PacketHelper.Failed(msg, message.Type, clientId));
            Logger.LogWarning(msg);
        }
        else if (room.Has(clientId))
        {
            var msg = $"Join failed: {clientId} is already in the game";
            await Router.Client.SendAsync(clientId, PacketHelper.Failed(msg, message.Type, clientId));
            Logger.LogWarning(msg);
        }
        else
        {
            var msg = $"Player {message.Name} joined room {room}";
            var player = await _playerService.ConnectById(room.Id, clientId, message.Name, message.Position);
            if (player == null)
            {
                var joinFailedMsg = $"Join failed. No such room: {message.RoomId}";
                await Router.Client.SendAsync(clientId, PacketHelper.Failed(joinFailedMsg, message.Type, clientId));
            }
            else
            {
                await Router.Client.SendAsync(clientId, PacketHelper.Success(msg, message.Type, clientId));
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