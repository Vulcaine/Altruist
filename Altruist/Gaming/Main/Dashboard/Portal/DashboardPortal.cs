/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.ThreeD;

namespace Altruist.Dashboard
{
    [Portal("dashboard")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    public class DashboardPortal : Portal
    {
        private readonly IGameWorldOrganizer3D _gameWorldOrganizer;
        private readonly IAltruistRouter _router;
        private readonly IConnectionManager _connectionManager;

        private readonly TimeSpan _fullSyncInterval = TimeSpan.FromSeconds(1.0);

        private DateTime _lastFullSyncUtc = DateTime.MinValue;

        private IEnumerable<AltruistConnection> _connections;

        public DashboardPortal(
            IGameWorldOrganizer3D gameWorldOrganizer,
            IAltruistRouter router,
            IConnectionManager connectionManager)
        {
            _gameWorldOrganizer = gameWorldOrganizer;
            _router = router;
            _connectionManager = connectionManager;
            _connections = Enumerable.Empty<AltruistConnection>();
        }

        public override async Task OnConnectedAsync(string clientId, ConnectionManager connectionManager, AltruistConnection connection)
        {
            await base.OnConnectedAsync(clientId, connectionManager, connection);
            _connections = await _connectionManager.GetConnectionsForPortal(this);
        }

        /// <summary>
        /// Periodic dashboard update:
        /// - every _fullSyncInterval, send a snapshot of all non-terrain
        ///   world objects (positions etc.) to all "dashboard" connections.
        /// </summary>
        [Cycle]
        public async Task UpdateDashboard()
        {
            if (_connections.Count() == 0)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now - _lastFullSyncUtc < _fullSyncInterval)
            {
                return;
            }

            _lastFullSyncUtc = now;

            foreach (var world in _gameWorldOrganizer.GetAllWorlds())
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

                foreach (var conn in _connections)
                {
                    await _router.Client.SendAsync(conn.ConnectionId, packet);
                }
            }
        }
    }

}
