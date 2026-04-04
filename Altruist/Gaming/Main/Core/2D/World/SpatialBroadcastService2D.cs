/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Gaming.TwoD;
using Altruist.Numerics;

namespace Altruist.Gaming.TwoD
{
    public interface ISpatialBroadcastService2D : ISpatialBroadcastService
    {
        Task SpatialBroadcast<T>(int worldIndex, IntVector2 position, IPacketBase packet) where T : IWorldObject2D;

        Task SmartSpatialBroadcast<T>(string senderClientId, int worldIndex, IntVector2 position, IPacketBase packet, int threshold) where T : IWorldObject2D;
    }

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
