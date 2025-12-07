// using Altruist.Gaming.ThreeD;
// using Altruist.Numerics;

// namespace Altruist.Gaming;

using Altruist;
using Altruist.Gaming.ThreeD;
using Altruist.Numerics;

public interface ISpatialBroadcastService3D
{
    Task SpatialBroadcast<T>(int worldIndex, IntVector3 position, IPacketBase packet) where T : IWorldObject3D;

    Task SmartSpatialBroadcast<T>(string senderClientId, int worldIndex, IntVector3 position, IPacketBase packet, int threshold) where T : IWorldObject3D;
}

[Service(typeof(ISpatialBroadcastService3D))]
[ConditionalOnConfig("altruist:game")]
[ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
public class SpatialBroadcastService3D : ISpatialBroadcastService3D
{
    private readonly IGameWorldOrganizer3D _gameWorldService;
    private readonly IAltruistRouter _router;

    private readonly ISocketManager _socketManager;

    public SpatialBroadcastService3D(IGameWorldOrganizer3D gameWorldService, IAltruistRouter router, ISocketManager socketManager)
    {
        _gameWorldService = gameWorldService;
        _router = router;
        _socketManager = socketManager;
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
    public async Task SpatialBroadcast<T>(int worldIndex, IntVector3 position, IPacketBase packet) where T : IWorldObject3D
    {
        var world = _gameWorldService.GetWorld(worldIndex);
        if (world != null)
        {
            var partitions = world.FindPartitionsForPosition(position.X, position.Y, position.Z, 0);

            foreach (var partition in partitions)
            {
                var clients = partition.GetAllObjects<T>();
                foreach (var client in clients)
                {
                    await _router.Client.SendAsync(client.ClientId, packet);
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
    public async Task SmartSpatialBroadcast<T>(
        string senderClientId, int worldIndex, IntVector3 position, IPacketBase packet, int threshold = 100) where T : IWorldObject3D
    {
        var room = await _socketManager.FindRoomForClientAsync(senderClientId);
        if (room != null && room.PlayerCount < threshold)
        {
            await _router.Room.SendAsync(room.Id, packet);
        }
        else
        {
            await SpatialBroadcast<T>(worldIndex, position, packet);
        }
    }
}
