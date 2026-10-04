using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Dashboard
{
    [Portal("/ws/dashboard")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class DashboardPortal : Portal, OnConnectingAsync, OnConnectedAsync, OnDisconnectedAsync
    {
        private readonly IGameWorldOrganizer3D _gameWorldOrganizer;
        private readonly IDashboardGizmoRegistry _gizmos;
        private readonly IAltruistRouter _router;
        private readonly IConnectionManager _connectionManager;
        private readonly IVisibilityTracker? _visibilityTracker;

        private readonly TimeSpan _fullSyncInterval = TimeSpan.FromMilliseconds(250);
        private DateTime _lastSyncUtc = DateTime.MinValue;

        private IEnumerable<AltruistConnection> _connections = Enumerable.Empty<AltruistConnection>();

        /// worldIndex → instanceId → last state
        private readonly Dictionary<int, Dictionary<string, DashboardWorldObjectStateDto>> _snapshots =
            new();

        private readonly Dictionary<int, HashSet<string>> _visibilityGizmoIds =
            new();

        public DashboardPortal(
            IGameWorldOrganizer3D gameWorldOrganizer,
            IDashboardGizmoRegistry gizmos,
            IAltruistRouter router,
            IConnectionManager connectionManager,
            IVisibilityTracker? visibilityTracker = null)
        {
            _gameWorldOrganizer = gameWorldOrganizer;
            _gizmos = gizmos;
            _router = router;
            _connectionManager = connectionManager;
            _visibilityTracker = visibilityTracker;
        }

        public Task OnConnectingAsync(
            string clientId,
            ConnectionManager connectionManager,
            AltruistConnection connection)
        {
            connection.SetId("dashboard");
            return Task.CompletedTask;
        }

        public async Task OnConnectedAsync(
            string clientId,
            ConnectionManager connectionManager,
            AltruistConnection connection)
        {
            _connections = await _connectionManager.GetConnectionsForPortal(this);
        }

        public async Task OnDisconnectedAsync(string clientId, Exception? exception)
        {
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
                var currentObjectIds = new HashSet<string>(StringComparer.Ordinal);

                foreach (var partition in world.FindPartitionsForPosition(0, 0, 0, float.MaxValue))
                {
                    var changedObjects = new List<DashboardWorldObjectStateDto>();

                    foreach (var obj in partition.GetAllObjects<IWorldObject3D>())
                    {
                        if (obj is Terrain || !ShouldIncludeObject(obj))
                            continue;

                        currentObjectIds.Add(obj.InstanceId);
                        var t = GetWorldObjectTransform(obj);

                        var current = new DashboardWorldObjectStateDto
                        {
                            InstanceId = obj.InstanceId,
                            Archetype = obj.ObjectArchetype ?? string.Empty,
                            Name = DashboardWorldObjectNames.Resolve(obj),
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

                var removedObjectIds = snapshot.Keys
                    .Where(instanceId => !currentObjectIds.Contains(instanceId))
                    .ToArray();

                foreach (var instanceId in removedObjectIds)
                    snapshot.Remove(instanceId);

                var visibilityGizmos = BuildVisibilityRadiusGizmos(world).ToArray();
                var removedVisibilityGizmos = DrainRemovedVisibilityGizmos(worldIndex, visibilityGizmos);
                var gizmoChanges = _gizmos.DrainChanges(worldIndex);
                var gizmos = gizmoChanges.Gizmos
                    .Concat(visibilityGizmos)
                    .ToArray();
                var removedGizmoIds = gizmoChanges.RemovedGizmoIds
                    .Concat(removedVisibilityGizmos)
                    .Distinct(StringComparer.Ordinal)
                    .ToArray();

                if (partitionDtos.Count == 0
                    && removedObjectIds.Length == 0
                    && gizmos.Length == 0
                    && removedGizmoIds.Length == 0)
                    continue;

                var packet = new DashboardWorldObjectStatePacket(
                    worldIndex,
                    now,
                    partitionDtos.ToArray(),
                    gizmos,
                    removedGizmoIds,
                    removedObjectIds
                );

                foreach (var conn in _connections)
                {
                    await _router.Client.SendAsync(conn.ConnectionId, packet);
                }
            }
        }

        private bool ShouldIncludeObject(IWorldObject3D obj)
        {
            if (_visibilityTracker is null)
                return true;

            if (!string.IsNullOrEmpty(obj.ClientId))
                return true;

            return _visibilityTracker.GetObserversOf(obj.InstanceId).Any();
        }

        private IEnumerable<DashboardGizmo> BuildVisibilityRadiusGizmos(IGameWorldManager3D world)
        {
            if (_visibilityTracker is null)
                yield break;

            var (_, lookup) = world.GetCachedSnapshot();
            foreach (var observer in _visibilityTracker.GetObservers())
            {
                if (observer is not IWorldObject3D observer3D)
                    continue;

                if (!lookup.ContainsKey(observer3D.InstanceId))
                    continue;

                var p = GetWorldObjectTransform(observer3D).Position;
                yield return new DashboardGizmo
                {
                    Id = BuildVisibilityGizmoId(observer3D),
                    WorldIndex = world.Index.Index,
                    Category = "visibility",
                    Source = "altruist",
                    Type = "circle",
                    Label = $"visibility radius {_visibilityTracker.ViewRange:F0}",
                    Color = "#22C55E66",
                    Position = new Vector3Dto { X = p.X, Y = p.Y, Z = p.Z },
                    Radius = _visibilityTracker.ViewRange,
                    AttachToInstanceId = observer3D.InstanceId,
                    UpdatedAtUtc = DateTime.UtcNow,
                };
            }
        }

        private string[] DrainRemovedVisibilityGizmos(int worldIndex, IReadOnlyCollection<DashboardGizmo> current)
        {
            if (!_visibilityGizmoIds.TryGetValue(worldIndex, out var previous))
            {
                _visibilityGizmoIds[worldIndex] = current
                    .Select(g => g.Id)
                    .ToHashSet(StringComparer.Ordinal);
                return Array.Empty<string>();
            }

            var currentIds = current
                .Select(g => g.Id)
                .ToHashSet(StringComparer.Ordinal);

            var removed = previous
                .Where(id => !currentIds.Contains(id))
                .ToArray();

            _visibilityGizmoIds[worldIndex] = currentIds;
            return removed;
        }

        private static string BuildVisibilityGizmoId(IWorldObject3D observer)
            => $"altruist:visibility:observer:{observer.InstanceId}:radius";

        private static Transform3D GetWorldObjectTransform(IWorldObject3D obj)
        {
            if (obj.Body is not IPhysxBody3D body)
                return obj.Transform;

            if (obj is IPhysicsTransformSync3D transformSync)
                return transformSync.GetWorldTransformFromPhysics(body);

            return obj.Transform
                .WithPosition(Position3D.From(body.Position))
                .WithRotation(Rotation3D.FromQuaternion(body.Rotation));
        }

        private static bool AreEqual(
            DashboardWorldObjectStateDto a,
            DashboardWorldObjectStateDto b)
        {
            if (a.InstanceId != b.InstanceId)
                return false;
            if (a.Archetype != b.Archetype)
                return false;
            if (!string.Equals(a.Name, b.Name, StringComparison.Ordinal))
                return false;

            return FloatEq(a.Position.X, b.Position.X)
                && FloatEq(a.Position.Y, b.Position.Y)
                && FloatEq(a.Position.Z, b.Position.Z);
        }

        private static bool FloatEq(float a, float b, float eps = 1e-4f)
            => Math.Abs(a - b) <= eps;
    }
}
