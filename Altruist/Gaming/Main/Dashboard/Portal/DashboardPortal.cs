using Altruist.Gaming.ThreeD;

namespace Altruist.Dashboard
{
    [Portal("dashboard")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class DashboardPortal : Portal
    {
        private readonly IGameWorldOrganizer3D _gameWorldOrganizer;
        private readonly IAltruistRouter _router;
        private readonly IConnectionManager _connectionManager;

        private readonly TimeSpan _fullSyncInterval = TimeSpan.FromSeconds(1);
        private DateTime _lastSyncUtc = DateTime.MinValue;

        private IEnumerable<AltruistConnection> _connections = Enumerable.Empty<AltruistConnection>();

        /// worldIndex → instanceId → last state
        private readonly Dictionary<int, Dictionary<string, DashboardWorldObjectStateDto>> _snapshots =
            new();

        public DashboardPortal(
            IGameWorldOrganizer3D gameWorldOrganizer,
            IAltruistRouter router,
            IConnectionManager connectionManager)
        {
            _gameWorldOrganizer = gameWorldOrganizer;
            _router = router;
            _connectionManager = connectionManager;
        }

        public override async Task OnConnectedAsync(
            string clientId,
            ConnectionManager connectionManager,
            AltruistConnection connection)
        {
            await base.OnConnectedAsync(clientId, connectionManager, connection);
            _connections = await _connectionManager.GetConnectionsForPortal(this);
        }

        [Cycle]
        public async Task UpdateDashboard()
        {
            if (!_connections.Any())
                return;

            var now = DateTime.UtcNow;
            if (now - _lastSyncUtc < _fullSyncInterval)
                return;

            _lastSyncUtc = now;

            foreach (var world in _gameWorldOrganizer.GetAllWorlds())
            {
                var worldIndex = world.Index.Index;

                if (!_snapshots.TryGetValue(worldIndex, out var snapshot))
                {
                    snapshot = new Dictionary<string, DashboardWorldObjectStateDto>();
                    _snapshots[worldIndex] = snapshot;
                }

                var partitionDtos = new List<DashboardPartitionStateDto>();

                foreach (var partition in world.FindPartitionsForPosition(0, 0, 0, float.MaxValue))
                {
                    var changedObjects = new List<DashboardWorldObjectStateDto>();

                    foreach (var obj in partition.GetAllObjects<IWorldObject3D>())
                    {
                        if (obj is Terrain)
                            continue;

                        var t = obj.Transform;

                        var current = new DashboardWorldObjectStateDto
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

                        if (!snapshot.TryGetValue(obj.InstanceId, out var last) ||
                            !AreEqual(last, current))
                        {
                            snapshot[obj.InstanceId] = current;
                            changedObjects.Add(current);
                        }
                    }

                    if (changedObjects.Count > 0)
                    {
                        partitionDtos.Add(new DashboardPartitionStateDto
                        {
                            X = partition.Index.X,
                            Y = partition.Index.Y,
                            Z = partition.Index.Z,
                            Objects = changedObjects
                        });
                    }
                }

                if (partitionDtos.Count == 0)
                    continue;

                var packet = new DashboardWorldObjectStatePacket(
                    worldIndex,
                    now,
                    partitionDtos
                );

                foreach (var conn in _connections)
                {
                    await _router.Client.SendAsync(conn.ConnectionId, packet);
                }
            }
        }

        private static bool AreEqual(
            DashboardWorldObjectStateDto a,
            DashboardWorldObjectStateDto b)
        {
            if (a.InstanceId != b.InstanceId)
                return false;
            if (a.Archetype != b.Archetype)
                return false;

            return FloatEq(a.Position.X, b.Position.X)
                && FloatEq(a.Position.Y, b.Position.Y)
                && FloatEq(a.Position.Z, b.Position.Z);
        }

        private static bool FloatEq(float a, float b, float eps = 1e-4f)
            => Math.Abs(a - b) <= eps;
    }
}
