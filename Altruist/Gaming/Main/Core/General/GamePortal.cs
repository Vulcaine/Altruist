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

[Service(typeof(GamePortalContext))]
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

public record JoinValidationResult(bool Success, string Message)
{
    public static JoinValidationResult Ok => new(true, string.Empty);
    public static JoinValidationResult Fail(string reason) => new(false, reason);
}

/// <summary>
/// Typed player service bound to a specific player entity used within this game session.
/// For heterogeneous player systems, use a discriminator on the stored entity and project
/// to runtime classes as needed.
/// </summary>
/// <summary>
/// Represents the main portal for a game session with typed player support.
/// 
/// <para>
/// This class defines the full connection flow for joining, leaving, and syncing with a game session. It includes
/// overridable template methods to enable extensibility without rewriting the entire logic. These are:
/// </para>
/// <list type="bullet">
///   <item><description><see cref="ValidateJoinRequestAsync"/> — Override to apply custom validation rules for joining.</description></item>
///   <item><description><see cref="ResolveJoinTargetAsync"/> — Customize room resolution logic.</description></item>
///   <item><description><see cref="ExecuteJoinAsync"/> — Define what happens after a successful room match.</description></item>
///   <item><description><see cref="OnJoinGameAsync"/> — Hook called after player successfully joins a room.</description></item>
///   <item><description><see cref="OnJoinGameFailedAsync"/> — Hook called when join attempt fails.</description></item>
///   <item><description><see cref="OnLeaveGameAsync"/> — Hook called when a player disconnects from a session.</description></item>
///   <item><description><see cref="FetchHandshakeRoomsAsync"/> / <see cref="BuildHandshakeResponseAsync"/> — Customize the handshake negotiation.</description></item>
/// </list>
/// </summary>
public abstract class AltruistGameSessionPortal<TPlayerEntity> : AltruistGamePortal<TPlayerEntity> where TPlayerEntity : PlayerEntity, new()
{
    protected AltruistGameSessionPortal(GamePortalContext context, GameWorldCoordinator gameWorld, IPlayerService<TPlayerEntity> playerService, ILoggerFactory loggerFactory) : base(context, gameWorld, playerService, loggerFactory)
    {
    }

    #region Handshake

    /// <summary>
    /// Handles the initial handshake when a client connects to the game session.
    /// Sends back available room information and any metadata needed to initialize the client.
    /// </summary>
    [Gate(IngressEP.Handshake)]
    public async virtual Task HandshakeAsync(HandshakePacket message, string clientId)
    {
        var rooms = await FetchHandshakeRoomsAsync(message, clientId);
        var response = await BuildHandshakeResponseAsync(message, clientId, rooms);
        await Router.Client.SendAsync(clientId, response);
    }

    /// <summary>
    /// Fetches the available rooms to be included in the handshake response.
    /// Override this to customize the list of rooms a client sees during handshake.
    /// </summary>
    protected virtual Task<Dictionary<string, RoomPacket>> FetchHandshakeRoomsAsync(HandshakePacket message, string clientId)
        => GetAllRoomsAsync();

    /// <summary>
    /// Constructs the handshake response packet from the list of available rooms.
    /// Override to include custom metadata or server capabilities in the handshake.
    /// </summary>
    protected virtual Task<HandshakePacket> BuildHandshakeResponseAsync(HandshakePacket message, string clientId, Dictionary<string, RoomPacket> rooms)
        => Task.FromResult(new HandshakePacket("server", rooms.Values.ToArray(), clientId));
    #endregion

    #region Leave Game

    /// <summary>
    /// Handles the logic for when a player leaves or disconnects from the game session.
    /// Cleans up player connection and updates the room state accordingly.
    /// </summary>
    [Gate(IngressEP.LeaveGame)]
    public async virtual Task ExitGameAsync(LeaveGamePacket message, string clientId)
    {
        var player = await _playerService.GetPlayerAsync(clientId);
        if (player == null) return;

        await _playerService.DisconnectAsync(clientId);
        await OnLeaveGameAsync(player);

        var room = await FindRoomForClientAsync(clientId);
        await HandleLeaveAsync(message, clientId, player, room);
    }

    /// <summary>
    /// Finalizes the leave operation by updating room state, broadcasting departure,
    /// and removing the player from any internal structures.
    /// Override to customize cleanup behavior.
    /// </summary>
    protected virtual async Task HandleLeaveAsync(LeaveGamePacket message, string clientId, TPlayerEntity player, RoomPacket? room)
    {
        var msg = $"Player {player.Name} left the game";
        _ = Router.Client.SendAsync(clientId, PacketHelper.Success(msg, clientId, message.Type));

        if (room != null)
        {
            var broadcastPacket = new LeaveGamePacket("server", clientId);
            room = room.RemoveConnection(clientId);
            _ = SaveRoomAsync(room);
            _ = Router.Room.SendAsync(room.Id, broadcastPacket);

            if (room.Empty())
                await DeleteRoomAsync(room.Id);
        }
    }
    #endregion

    #region Join Game

    /// <summary>
    /// Handles the main join flow — validates input, resolves a room, and connects the player.
    /// Override helper methods to customize each step of the join pipeline.
    /// </summary>
    [Gate(IngressEP.JoinGame)]
    public async virtual Task JoinGameAsync(JoinGamePacket message, string clientId)
    {
        var validation = await ValidateJoinRequestAsync(message, clientId);
        if (!validation.Success)
        {
            Logger.LogWarning("JoinGame failed: {Reason} (ClientId: {ClientId})", validation.Message, clientId);
            await Router.Client.SendAsync(clientId, PacketHelper.Failed(validation.Message, clientId, message.Type));
            await OnJoinGameFailedAsync(clientId, message, validation.Message);
            return;
        }

        var room = await ResolveJoinTargetAsync(message, clientId);
        if (room == null)
        {
            var msg = "Join failed: No available or valid room";
            Logger.LogWarning("JoinGame failed: {Reason} (ClientId: {ClientId})", msg, clientId);
            await Router.Client.SendAsync(clientId, PacketHelper.Failed(msg, clientId, message.Type));
            await OnJoinGameFailedAsync(clientId, message, msg);
            return;
        }

        await ExecuteJoinAsync(room, message, clientId);
    }

    protected record JoinValidationResult(bool Success, string Message)
    {
        public static JoinValidationResult Ok => new(true, string.Empty);
        public static JoinValidationResult Fail(string reason) => new(false, reason);
    }

    /// <summary>
    /// Validates the join request (e.g. username, input format).
    /// Override this to implement custom join preconditions like authentication, rate limits, or name filters.
    /// </summary>
    protected virtual Task<JoinValidationResult> ValidateJoinRequestAsync(JoinGamePacket msg, string clientId)
    {
        if (string.IsNullOrWhiteSpace(msg.Name))
            return Task.FromResult(JoinValidationResult.Fail("Username is required!"));

        return Task.FromResult(JoinValidationResult.Ok);
    }

    /// <summary>
    /// Resolves which room the client should join based on the join packet.
    /// Override to implement advanced room selection logic such as matchmaking, load balancing, etc.
    /// </summary>
    protected virtual async Task<RoomPacket?> ResolveJoinTargetAsync(JoinGamePacket msg, string clientId)
    {
        if (!string.IsNullOrEmpty(msg.RoomId))
            return await GetRoomAsync(msg.RoomId);

        return await FindAvailableRoomAsync();
    }

    /// <summary>
    /// Executes the actual join — adds the player to the room, updates game state, and notifies the client.
    /// Override to inject custom behavior when the player enters a room.
    /// </summary>
    protected virtual async Task ExecuteJoinAsync(RoomPacket room, JoinGamePacket msg, string clientId)
    {
        if (room.Has(clientId))
        {
            var error = $"Join failed: {clientId} is already in the room.";
            Logger.LogWarning("JoinGame failed: {Reason} (ClientId: {ClientId})", error, clientId);
            await Router.Client.SendAsync(clientId, PacketHelper.Failed(error, clientId, msg.Type));
            await OnJoinGameFailedAsync(clientId, msg, error);
            return;
        }

        var player = await _playerService.ConnectById(room.Id, clientId, msg.Name, msg.WorldIndex ?? 0, msg.Position);
        if (player == null)
        {
            var failMsg = "Join failed: could not create player.";
            Logger.LogWarning("JoinGame failed: {Reason} (ClientId: {ClientId})", failMsg, clientId);
            await Router.Client.SendAsync(clientId, PacketHelper.Failed(failMsg, clientId, msg.Type));
            await OnJoinGameFailedAsync(clientId, msg, failMsg);
            return;
        }

        await Router.Client.SendAsync(clientId, PacketHelper.Success($"Joined room {room.Id}.", clientId, msg.Type));
        await Router.Synchronize.SendAsync(player, forceAllAsChanged: true);
        await OnJoinGameAsync(player, room);

        var conn = await GetConnectionAsync(clientId);
        if (conn != null)
        {
            conn.ConnectionState = ConnectionStates.Joined;
        }
        // TODO: we must save the updated connection back to the cache.

        // enable player updates
        player.Activate();
    }
    #endregion

    #region Cleanup

    /// <summary>
    /// Cleans up portal and player service state. Invoked when the server shuts down or the portal is disposed.
    /// You can override this if you want to extend or wrap the cleanup logic.
    /// </summary>
    public override async Task Cleanup()
    {
        try
        {
            await _context.Cleanup();
        }
        catch (Exception ex)
        {
            Logger.LogError("Error cleaning up connections: {Exception}", ex);
        }
        await _playerService.Cleanup();
    }
    #endregion

    #region Extension Hooks

    /// <summary>
    /// Triggered after a player successfully joins a room. Use this to set player state,
    /// broadcast join messages, or initialize player-related data.
    /// </summary>
    protected virtual Task OnJoinGameAsync(TPlayerEntity player, RoomPacket room) => Task.CompletedTask;

    /// <summary>
    /// Triggered when a join request fails. Use this to log analytics,
    /// inform other systems, or audit failed attempts.
    /// </summary>
    protected virtual Task OnJoinGameFailedAsync(string clientId, JoinGamePacket message, string reason) => Task.CompletedTask;

    /// <summary>
    /// Triggered when a player is disconnected or leaves the game session.
    /// Override to run post-leave operations like persisting player state or freeing resources.
    /// </summary>
    protected virtual Task OnLeaveGameAsync(TPlayerEntity player) => Task.CompletedTask;

    #endregion
}