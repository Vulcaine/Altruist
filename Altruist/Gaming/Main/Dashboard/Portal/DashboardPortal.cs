/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.ThreeD;

namespace Altruist.Dashboard
{
    [Portal("/dashboard")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    public class DashboardPortal : Portal
    {
        private readonly IGameWorldOrganizer3D _gameWorldOrganizer;
        private readonly IAltruistRouter _router;
        private readonly IConnectionManager _connectionManager;

        private readonly TimeSpan _fullSyncInterval = TimeSpan.FromSeconds(1.0);

        private DateTime _lastFullSyncUtc = DateTime.MinValue;

        public DashboardPortal(
            IGameWorldOrganizer3D gameWorldOrganizer,
            IAltruistRouter router,
            IConnectionManager connectionManager)
        {
            _gameWorldOrganizer = gameWorldOrganizer;
            _router = router;
            _connectionManager = connectionManager;
        }

        /// <summary>
        /// Periodic dashboard update:
        /// - every _fullSyncInterval, send a snapshot of all non-terrain
        ///   world objects (positions etc.) to all "dashboard" connections.
        /// </summary>
        [Cycle]
        public async Task UpdateDashboard()
        {
            var now = DateTime.UtcNow;
            if (now - _lastFullSyncUtc < _fullSyncInterval)
            {
                return;
            }

            _lastFullSyncUtc = now;

            var worlds = _gameWorldOrganizer.GetAllWorlds();
            var connectionsCursor = await _connectionManager.GetAllConnectionsAsync();
            var dashboardConnections = await _connectionManager.GetConnectionsForPortal(this);

            if (dashboardConnections.Count() == 0)
            {
                return;
            }

            foreach (var world in worlds)
            {
                var worldObjects = world
                    .FindAllObjects<IWorldObject3D>()
                    .Where(o => o is not Terrain)
                    .ToList();

                if (worldObjects.Count == 0)
                    continue;

                var objectStates = new List<DashboardWorldObjectStateDto>(worldObjects.Count);

                foreach (var obj in worldObjects)
                {
                    var t = obj.Transform;
                    objectStates.Add(new DashboardWorldObjectStateDto
                    {
                        InstanceId = obj.InstanceId,
                        Archetype = obj.Archetype ?? string.Empty,
                        Position = new Vector3Dto
                        {
                            X = t.Position.X,
                            Y = t.Position.Y,
                            Z = t.Position.Z
                        }
                    });
                }

                var packet = new DashboardWorldObjectStatePacket(
                    worldIndex: world.Index.Index,
                    timestampUtc: now,
                    objects: objectStates);

                foreach (var conn in dashboardConnections)
                {
                    await _router.Client.SendAsync(conn.ConnectionId, packet);
                }
            }
        }
    }

}
