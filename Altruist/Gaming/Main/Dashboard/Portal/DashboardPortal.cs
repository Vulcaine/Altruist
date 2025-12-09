/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.ThreeD;

namespace Altruist.Dashboard
{
    [Portal("dashboard")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public class DashboardPortal : Portal
    {
        private readonly IGameWorldOrganizer3D _gameWorldOrganizer;
        private readonly IAltruistRouter _router;
        private readonly IConnectionManager _connectionManager;

        private readonly TimeSpan _fullSyncInterval = TimeSpan.FromSeconds(1.0);
        private DateTime _lastFullSyncUtc = DateTime.MinValue;

        private IEnumerable<AltruistConnection> _connections;

        /// <summary>
        /// Per-world snapshot of last sent object states.
        /// worldIndex -> (instanceId -> lastState)
        /// </summary>
        private readonly Dictionary<int, Dictionary<string, DashboardWorldObjectStateDto>> _lastWorldSnapshots =
            new Dictionary<int, Dictionary<string, DashboardWorldObjectStateDto>>();

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

        public override async Task OnConnectedAsync(
            string clientId,
            ConnectionManager connectionManager,
            AltruistConnection connection)
        {
            await base.OnConnectedAsync(clientId, connectionManager, connection);
            _connections = await _connectionManager.GetConnectionsForPortal(this);
        }

        /// <summary>
        /// Periodic dashboard update:
        /// - every _fullSyncInterval, scan all non-terrain world objects
        /// - build per-object state
        /// - send ONLY the ones that changed since last snapshot
        /// </summary>
        [Cycle]
        public async Task UpdateDashboard()
        {
            if (!_connections.Any())
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

                // No objects at all in this world -> nothing to send
                if (worldObjects.Count == 0)
                    continue;

                var worldIndex = world.Index.Index;

                if (!_lastWorldSnapshots.TryGetValue(worldIndex, out var lastSnapshotForWorld))
                {
                    lastSnapshotForWorld = new Dictionary<string, DashboardWorldObjectStateDto>();
                    _lastWorldSnapshots[worldIndex] = lastSnapshotForWorld;
                }

                var changedStates = new List<DashboardWorldObjectStateDto>();

                foreach (var obj in worldObjects)
                {
                    var t = obj.Transform;

                    var currentState = new DashboardWorldObjectStateDto
                    {
                        InstanceId = obj.InstanceId,
                        Archetype = obj.Archetype ?? string.Empty,
                        Position = new Vector3Dto
                        {
                            X = t.Position.X,
                            Y = t.Position.Y,
                            Z = t.Position.Z
                        }
                    };

                    if (!lastSnapshotForWorld.TryGetValue(obj.InstanceId, out var lastState) ||
                        !AreEqual(lastState, currentState))
                    {
                        // State changed or new object -> remember and mark for sending
                        lastSnapshotForWorld[obj.InstanceId] = currentState;
                        changedStates.Add(currentState);
                    }
                }

                // If nothing changed for this world, skip sending a packet.
                if (changedStates.Count == 0)
                    continue;

                var packet = new DashboardWorldObjectStatePacket(
                    worldIndex: worldIndex,
                    timestampUtc: now,
                    objects: changedStates);

                foreach (var conn in _connections)
                {
                    await _router.Client.SendAsync(conn.ConnectionId, packet);
                }
            }
        }

        // --------------------------------------------------------------------
        // Snapshot equality helpers
        // --------------------------------------------------------------------

        private static bool AreEqual(
            DashboardWorldObjectStateDto a,
            DashboardWorldObjectStateDto b)
        {
            if (!string.Equals(a.InstanceId, b.InstanceId, StringComparison.Ordinal))
                return false;

            if (!string.Equals(a.Archetype, b.Archetype, StringComparison.Ordinal))
                return false;

            return FloatEqual(a.Position.X, b.Position.X)
                && FloatEqual(a.Position.Y, b.Position.Y)
                && FloatEqual(a.Position.Z, b.Position.Z);
        }

        private static bool FloatEqual(float a, float b, float epsilon = 1e-4f)
        {
            return Math.Abs(a - b) <= epsilon;
        }
    }
}
