/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Gaming.TwoD;
using Altruist.Numerics;

namespace Altruist.Gaming.TwoD
{
    /// <summary>2D <see cref="ISpatialBroadcastService"/>: besides observer-based sends
    /// (<see cref="ISpatialBroadcastService.SendToObserversAsync"/>, the preferred way to replicate an entity's
    /// state), it can broadcast to the clients owning objects in the partition(s) at a position. Use the
    /// spatial variants only for cheap, non-critical effects; use match-room broadcasts for room-based games.
    /// The 3D counterpart is <see cref="ISpatialBroadcastService3D"/>.</summary>
    public interface ISpatialBroadcastService2D : ISpatialBroadcastService
    {
        /// <summary>Sends <paramref name="packet"/> to the <c>ClientId</c> of every <typeparamref name="T"/>
        /// object filed in the partition(s) touching <paramref name="position"/> in world
        /// <paramref name="worldIndex"/> (no-op for an unknown world). Non-critical, ephemeral events only.</summary>
        /// <typeparam name="T">The object type whose owners receive the packet (typically the player type).</typeparam>
        /// <param name="worldIndex">Numeric world index.</param>
        /// <param name="position">World position, world units.</param>
        /// <param name="packet">Packet to send.</param>
        Task SpatialBroadcast<T>(int worldIndex, IntVector2 position, IPacketBase packet) where T : IWorldObject2D;

        /// <summary>Sends to the sender's whole room when it has fewer than <paramref name="threshold"/>
        /// players, otherwise falls back to <see cref="SpatialBroadcast{T}"/>. Non-critical events only.</summary>
        /// <typeparam name="T">The object type whose owners receive the packet in the spatial fallback.</typeparam>
        /// <param name="senderClientId">Client whose room is looked up.</param>
        /// <param name="worldIndex">Numeric world index for the spatial fallback.</param>
        /// <param name="position">World position for the spatial fallback.</param>
        /// <param name="packet">Packet to send.</param>
        /// <param name="threshold">Room size from which the spatial fallback is used (the implementation defaults it to 100).</param>
        Task SmartSpatialBroadcast<T>(string senderClientId, int worldIndex, IntVector2 position, IPacketBase packet, int threshold) where T : IWorldObject2D;
    }

    /// <summary>Default <see cref="ISpatialBroadcastService2D"/> (singleton, also registered as
    /// <see cref="ISpatialBroadcastService"/>, when <c>altruist:game</c> exists and
    /// <c>altruist:environment:mode</c> is <c>2D</c>). Sends one packet per recipient through the router,
    /// awaiting each in turn.</summary>
    [Service(typeof(ISpatialBroadcastService2D))]
    [Service(typeof(ISpatialBroadcastService))]
    [ConditionalOnConfig("altruist:game")]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    public class SpatialBroadcastService2D : ISpatialBroadcastService2D
    {
        private readonly IGameWorldOrganizer2D _gameWorldOrganizer;
        private readonly IAltruistRouter _router;
        private readonly ISocketManager _socketManager;
        private readonly IVisibilityTracker? _visibilityTracker;

        /// <summary>DI constructor.</summary>
        /// <param name="gameWorldOrganizer">Resolves worlds by index.</param>
        /// <param name="router">Sends packets to clients and rooms.</param>
        /// <param name="socketManager">Finds the sender's room for <see cref="SmartSpatialBroadcast{T}"/>.</param>
        /// <param name="visibilityTracker">Optional; without it <see cref="SendToObserversAsync"/> sends nothing.</param>
        public SpatialBroadcastService2D(
            IGameWorldOrganizer2D gameWorldOrganizer,
            IAltruistRouter router,
            ISocketManager socketManager,
            IVisibilityTracker? visibilityTracker = null)
        {
            _gameWorldOrganizer = gameWorldOrganizer;
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
        /// ⚠️ Best suited for non-critical, ephemeral events like emotes or short-lived effects.
        /// ❌ Do not use for gameplay-critical state (item drops/removals) — may cause state desync.
        /// </summary>
        /// <typeparam name="T">The object type whose owners receive the packet.</typeparam>
        /// <param name="worldIndex">Numeric world index.</param>
        /// <param name="position">World position, world units.</param>
        /// <param name="packet">Packet to send.</param>
        public async Task SpatialBroadcast<T>(int worldIndex, IntVector2 position, IPacketBase packet)
            where T : IWorldObject2D
        {
            var world = _gameWorldOrganizer.GetWorld(worldIndex);
            if (world is null)
                return;

            var partitions = world.FindPartitionsForPosition(position.X, position.Y, 0);

            foreach (var partition in partitions)
            {
                if (partition is not WorldPartition2D p2d)
                    continue;

                foreach (var client in p2d.GetAllObjects<T>())
                {
                    await _router.Client.SendAsync(client.ClientId, packet);
                }
            }
        }

        /// <summary>
        /// Sends a packet to clients intelligently based on room size.
        ///
        /// ✅ Below threshold: broadcasts to the entire room.
        /// 🔁 Above threshold: uses spatial partitioning to reach only nearby clients.
        ///
        /// ⚠️ Use for non-critical broadcasts. Avoid for persistent state events.
        /// </summary>
        /// <typeparam name="T">The object type whose owners receive the packet in the spatial fallback.</typeparam>
        /// <param name="senderClientId">Client whose room is looked up.</param>
        /// <param name="worldIndex">Numeric world index for the spatial fallback.</param>
        /// <param name="position">World position for the spatial fallback.</param>
        /// <param name="packet">Packet to send.</param>
        /// <param name="threshold">Room size from which the spatial fallback is used (default 100).</param>
        public async Task SmartSpatialBroadcast<T>(
            string senderClientId,
            int worldIndex,
            IntVector2 position,
            IPacketBase packet,
            int threshold = 100)
            where T : IWorldObject2D
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
}
