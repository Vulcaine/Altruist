// using Altruist.Numerics;

// namespace Altruist.Gaming.TwoD;

// public interface ISpatialBroadcastService2D : ISpatialBroadcastService
// {
//     // Task SpatialBroadcast(string initiatorClientId, IntVector2 position, IPacketBase packet);

//     // Task SmartSpatialBroadcast(string senderClientId, IntVector2 position, IPacketBase packet, int threshold = 100);
// }

// [Service(typeof(ISpatialBroadcastService))]
// [Service(typeof(ISpatialBroadcastService2D))]
// [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "2D")]
// public class SpatialBroadcastService2D : ISpatialBroadcastService2D
// {
//     private readonly IGameWorldService _gameWorldService;
//     private readonly IAltruistRouter _router;

//     private readonly ISocketManager _socketManager;

//     public SpatialBroadcastService2D(IGameWorldService gameWorldService, IAltruistRouter router, ISocketManager socketManager)
//     {
//         _gameWorldService = gameWorldService;
//         _router = router;
//         _socketManager = socketManager;
//     }

//     /// <summary>
//     /// Broadcasts a packet to all clients in nearby partitions based on world position.
//     /// 
//     /// ⚠️ This method is best suited for **non-critical, ephemeral events** like emotes, chat bubbles,
//     /// or short-lived visual effects where consistency isn't required.
//     /// 
//     /// ❌ Do not use this for gameplay-critical state such as item drops or removals. 
//     /// Since client proximity is calculated per broadcast, it's possible a client receives a spawn 
//     /// event but moves out of the partition before the removal is sent, leading to **state desync**.
//     /// 
//     /// </summary>
//     /// <param name="initiatorClientId">The client initiating the event.</param>
//     /// <param name="x">The X coordinate in the world.</param>
//     /// <param name="y">The Y coordinate in the world.</param>
//     /// <param name="packet">The packet to be broadcasted.</param>
//     // public async Task SpatialBroadcast(string initiatorClientId, IntVector2 position, IPacketBase packet)
//     // {
//     //     var world = await _gameWorldService.FindWorldForClientAsync(initiatorClientId) as GameWorldManager2D;
//     //     if (world != null)
//     //     {
//     //         var partitions = world.FindPartitionsForPosition(position.X, position.Y, 0);
//     //         packet.Header = PacketHeaders.Broadcast;

//     //         foreach (var partition in partitions)
//     //         {
//     //             var clients = partition.GetObjectsByType(WorldObjectTypeKeys.Client);
//     //             foreach (var client in clients)
//     //             {
//     //                 await _router.Client.SendAsync(client.InstanceId, packet);
//     //             }
//     //         }
//     //     }
//     // }

//     /// <summary>
//     /// Sends a packet to clients intelligently based on room size.
//     /// 
//     /// ✅ If the room the sender belongs to has fewer players than the specified threshold,
//     /// the packet is broadcast to the entire room.
//     /// 
//     /// 🔁 If the room exceeds the threshold, spatial partitioning takes place, to only send the packet
//     /// to nearby clients, based on the sender's coordinates.
//     /// 
//     /// ⚠️ Use this method for **non-critical broadcasts** such as visual effects, chat bubbles, emotes,
//     /// or area-based announcements where consistency is not essential.
//     /// 
//     /// ❌ Avoid using this for persistent or stateful game events like item drops or removals,
//     /// as players may move out of the relevant partitions between state changes, resulting in
//     /// inconsistencies (e.g., a player sees a dropped item but never receives the removal).
//     /// 
//     /// </summary>
//     /// <param name="senderClientId">The ID of the client sending the packet.</param>
//     /// <param name="x">The X coordinate for spatial partition lookup.</param>
//     /// <param name="y">The Y coordinate for spatial partition lookup.</param>
//     /// <param name="packet">The packet to be sent to clients.</param>
//     /// <param name="threshold">
//     /// The maximum number of players in a room before switching to spatial broadcast.
//     /// Defaults to 100.
//     /// </param>
//     /// <returns>A task representing the asynchronous operation.</returns>
//     // public async Task SmartSpatialBroadcast(string senderClientId, IntVector2 position, IPacketBase packet, int threshold = 100)
//     // {
//     //     var room = await _socketManager.FindRoomForClientAsync(senderClientId);
//     //     if (room != null && room.PlayerCount < threshold)
//     //     {
//     //         await _router.Room.SendAsync(room.Id, packet);
//     //     }
//     //     else
//     //     {
//     //         await SpatialBroadcast(senderClientId, position, packet);
//     //     }
//     // }
// }