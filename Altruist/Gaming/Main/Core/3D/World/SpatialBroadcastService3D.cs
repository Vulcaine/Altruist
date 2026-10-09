// using Altruist.Gaming.ThreeD;
// using Altruist.Numerics;

// namespace Altruist.Gaming;

using Altruist;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Numerics;

/// <summary>
/// 3D broadcasting helpers: visibility-aware sends to an entity's observers (<see cref="ISpatialBroadcastService.SendToObserversAsync"/>)
/// and partition-based sends around a world position.
/// </summary>
/// <remarks>
/// Prefer <see cref="ISpatialBroadcastService.SendToObserversAsync"/> for anything tied to an entity; it is consistent with the
/// spawn/despawn events of the visibility tracker. Use the partition-based methods only for ephemeral effects.
/// For 2D use <see cref="Altruist.Gaming.TwoD.ISpatialBroadcastService2D"/>. Note: this type is declared in the global namespace.
/// </remarks>
public interface ISpatialBroadcastService3D : ISpatialBroadcastService
{
    /// <summary>
    /// Sends <paramref name="packet"/> to the <c>ClientId</c> of every <typeparamref name="T"/> object filed in the
    /// partition(s) containing <paramref name="position"/>. Best effort, for ephemeral events only.
    /// </summary>
    /// <typeparam name="T">World object type that represents a connected client (e.g. the player type).</typeparam>
    /// <param name="worldIndex">World index; unknown worlds are ignored.</param>
    /// <param name="position">World position (integer world units).</param>
    /// <param name="packet">Packet to send.</param>
    Task SpatialBroadcast<T>(int worldIndex, IntVector3 position, IPacketBase packet) where T : IWorldObject3D;

    /// <summary>
    /// Sends to the sender's whole socket room when it has fewer than <paramref name="threshold"/> players, otherwise
    /// falls back to <see cref="SpatialBroadcast{T}"/> around <paramref name="position"/>.
    /// </summary>
    /// <typeparam name="T">World object type that represents a connected client.</typeparam>
    /// <param name="senderClientId">Client whose socket room is checked.</param>
    /// <param name="worldIndex">World index for the spatial fallback.</param>
    /// <param name="position">World position for the spatial fallback.</param>
    /// <param name="packet">Packet to send.</param>
    /// <param name="threshold">Room size at or above which the spatial fallback is used.</param>
    Task SmartSpatialBroadcast<T>(string senderClientId, int worldIndex, IntVector3 position, IPacketBase packet, int threshold) where T : IWorldObject3D;
}

/// <summary>
/// Default <see cref="ISpatialBroadcastService3D"/> (also registered as <see cref="ISpatialBroadcastService"/>) when
/// <c>altruist:game</c> is configured and <c>altruist:environment:mode</c> is <c>3D</c>.
/// </summary>
[Service(typeof(ISpatialBroadcastService3D))]
[Service(typeof(ISpatialBroadcastService))]
[ConditionalOnConfig("altruist:game")]
[ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
public class SpatialBroadcastService3D : ISpatialBroadcastService3D
{
    private readonly IGameWorldOrganizer3D _gameWorldService;
    private readonly IAltruistRouter _router;
    private readonly ISocketManager _socketManager;
    private readonly IVisibilityTracker? _visibilityTracker;

    /// <summary>Creates the service.</summary>
    /// <param name="gameWorldService">World organizer used to resolve worlds by index.</param>
    /// <param name="router">Router used to send packets to clients and rooms.</param>
    /// <param name="socketManager">Socket manager used to find a client's room.</param>
    /// <param name="visibilityTracker">Optional tracker; without it <see cref="SendToObserversAsync"/> is a no-op.</param>
    public SpatialBroadcastService3D(
        IGameWorldOrganizer3D gameWorldService,
        IAltruistRouter router,
        ISocketManager socketManager,
        IVisibilityTracker? visibilityTracker = null)
    {
        _gameWorldService = gameWorldService;
        _router = router;
        _socketManager = socketManager;
        _visibilityTracker = visibilityTracker;
    }

    /// <inheritdoc/>
    public async Task SendToObserversAsync(string entityInstanceId, IPacketBase packet)
    {
        if (_visibilityTracker == null) return;

        foreach (var observerClientId in _visibilityTracker.GetObserversOf(entityInstanceId))
        {
            await _router.Client.SendAsync(observerClientId, packet);
        }
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
    /// <typeparam name="T">World object type that represents a connected client (e.g. the player type).</typeparam>
    /// <param name="worldIndex">World index; unknown worlds are ignored.</param>
    /// <param name="position">World position (integer world units); the partition(s) containing it are used (radius 0).</param>
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
    /// <typeparam name="T">World object type that represents a connected client.</typeparam>
    /// <param name="senderClientId">The ID of the client sending the packet.</param>
    /// <param name="worldIndex">World index for the spatial fallback.</param>
    /// <param name="position">World position for spatial partition lookup.</param>
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
