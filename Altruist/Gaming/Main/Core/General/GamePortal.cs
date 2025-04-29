/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

public class GamePortalContext : PortalContext
{
    public IPlayerCursorFactory PlayerCursorFactory { get; }

    public GamePortalContext(IAltruistContext altruistContext, IServiceProvider serviceProvider) : base(altruistContext, serviceProvider)
    {
        PlayerCursorFactory = serviceProvider.GetRequiredService<IPlayerCursorFactory>();
    }
}

public abstract class AltruistGamePortal<TPlayerEntity> : Portal<GamePortalContext> where TPlayerEntity : PlayerEntity, new()
{
    protected readonly GameWorldCoordinator _worldCoordinator;
    protected readonly IPlayerService<TPlayerEntity> _playerService;

    protected AltruistGamePortal(GamePortalContext context,
        GameWorldCoordinator worldCoordinator,
        IPlayerService<TPlayerEntity> playerService,
        ILoggerFactory loggerFactory) : base(context, loggerFactory)
    {
        _worldCoordinator = worldCoordinator;
        _playerService = playerService;
    }

    /// <summary>
    /// Finds the game world manager associated with the given client ID.
    /// </summary>
    /// <param name="clientId">The client ID to search for.</param>
    /// <returns>The game world manager associated with the client ID, or null.</returns>
    protected async Task<GameWorldManager?> FindWorldForClientAsync(string clientId)
    {
        var player = await _playerService.GetPlayerAsync(clientId);
        if (player != null)
        {
            var world = _worldCoordinator.GetWorld(player.WorldIndex);
            if (world != null)
            {
                return world;
            }
        }

        return null;
    }

    /// <summary>
    /// Broadcasts a packet to all clients in nearby partitions based on world position.
    /// 
    /// ⚠️ This method is best suited for **non-critical, ephemeral events** like emotes, chat bubbles,
    /// or short-lived visual effects where consistency isn't required.
    /// 
    /// ❌ Do not use this for gameplay-critical state such as item drops or removals. 
    /// Since client proximity is calculated per broadcast, it's possible a client receives a spawn 
    /// event but moves out of the partition before the removal is sent, leading to **state desync**.
    /// 
    /// </summary>
    /// <param name="initiatorClientId">The client initiating the event.</param>
    /// <param name="x">The X coordinate in the world.</param>
    /// <param name="y">The Y coordinate in the world.</param>
    /// <param name="packet">The packet to be broadcasted.</param>
    protected async Task SpatialBroadcast(string initiatorClientId, int x, int y, IPacketBase packet)
    {
        var world = await FindWorldForClientAsync(initiatorClientId);
        if (world != null)
        {
            var partitions = world.FindPartitionsForPosition(x, y, 0);
            packet.Header = PacketHeaders.Broadcast;

            foreach (var partition in partitions)
            {
                var clients = partition.GetObjectsByType(WorldObjectTypeKeys.Client);
                foreach (var client in clients)
                {
                    await Router.Client.SendAsync(client.InstanceId, packet);
                }
            }
        }
    }

    /// <summary>
    /// Sends a packet to clients intelligently based on room size.
    /// 
    /// ✅ If the room the sender belongs to has fewer players than the specified threshold,
    /// the packet is broadcast to the entire room.
    /// 
    /// 🔁 If the room exceeds the threshold, spatial partitioning takes place, to only send the packet
    /// to nearby clients, based on the sender's coordinates.
    /// 
    /// ⚠️ Use this method for **non-critical broadcasts** such as visual effects, chat bubbles, emotes,
    /// or area-based announcements where consistency is not essential.
    /// 
    /// ❌ Avoid using this for persistent or stateful game events like item drops or removals,
    /// as players may move out of the relevant partitions between state changes, resulting in
    /// inconsistencies (e.g., a player sees a dropped item but never receives the removal).
    /// 
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
    protected async Task SmartSpatialBroadcast(string senderClientId, int x, int y, IPacketBase packet, int threshold = 100)
    {
        var room = await FindRoomForClientAsync(senderClientId);
        if (room != null && room.PlayerCount < threshold)
        {
            await Router.Room.SendAsync(room.Id, packet);
        }
        else
        {
            await SpatialBroadcast(senderClientId, x, y, packet);
        }
    }
}

public abstract class AltruistGameSessionPortal<TPlayerEntity> : AltruistGamePortal<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    protected AltruistGameSessionPortal(GamePortalContext context, GameWorldCoordinator gameWorld, IPlayerService<TPlayerEntity> playerService, ILoggerFactory loggerFactory) : base(context, gameWorld, playerService, loggerFactory)
    {
    }

    [Gate(IngressEP.Handshake)]
    public async virtual Task HandshakeAsync(HandshakePacket message, string clientId)
    {
        var rooms = await GetAllRoomsAsync();
        // TODO: fill out user token
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
            await Router.Client.SendAsync(clientId, PacketHelper.Failed("Username is required!", clientId, message.Type));
            return;
        }

        RoomPacket? room;
        if (!string.IsNullOrEmpty(message.RoomId))
        {
            room = await GetRoomAsync(message.RoomId);

            if (room == null)
            {
                var joinFailedMsg = $"Join failed. No such room: {message.RoomId}";
                await Router.Client.SendAsync(clientId, PacketHelper.Failed(joinFailedMsg, clientId, message.Type));
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
            await Router.Client.SendAsync(clientId, PacketHelper.Failed(msg, clientId, message.Type));
            Logger.LogWarning(msg);
        }
        else if (room.Has(clientId))
        {
            var msg = $"Join failed: {clientId} is already in the game";
            await Router.Client.SendAsync(clientId, PacketHelper.Failed(msg, clientId, message.Type));
            Logger.LogWarning(msg);
        }
        else
        {
            var msg = $"Player {message.Name} joined the room: {room.Id}.";
            var player = await _playerService.ConnectById(room.Id, clientId, message.Name, message.WorldIndex ?? 0, message.Position);
            if (player == null)
            {
                var joinFailedMsg = $"Join failed. No such room: {message.RoomId}";
                await Router.Client.SendAsync(clientId, PacketHelper.Failed(joinFailedMsg, clientId, message.Type));
            }
            else
            {
                await Router.Client.SendAsync(clientId, PacketHelper.Success(msg, clientId, message.Type));
                await Router.Synchronize.SendAsync(player, forceAllAsChanged: true);
            }

            Logger.LogInformation(msg);
        }
    }

    public override async Task Cleanup()
    {
        try
        {
            await _context.Cleanup();
        }
        catch (Exception ex)
        {
            Logger.LogError($"Error cleaning up connections: {ex}");
        }
        await _playerService.Cleanup();
    }
}