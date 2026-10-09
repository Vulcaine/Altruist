using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Dashboard
{
    /// <summary>
    /// Websocket portal at <c>/ws/dashboard</c> that pushes live 3D world state deltas to the dashboard viewer as
    /// <see cref="DashboardWorldObjectStatePacket"/>s: changed object positions per partition, removed objects, and gizmo
    /// changes from <see cref="IDashboardGizmoRegistry"/> plus auto-generated visibility-radius circles.
    /// </summary>
    /// <remarks>
    /// <para>Active only when <c>altruist:dashboard:enabled = true</c> and the <c>Altruist.Dashboard</c> assembly is loaded. The
    /// full snapshot (with colliders and terrain) is served over HTTP by <see cref="WorldDashboardController"/>; this portal
    /// only streams deltas, so a viewer should load the snapshot first and then apply packets. Framework-internal: games do
    /// not call it, they publish debug visuals through <see cref="IDashboardGizmoRegistry"/>.</para>
    /// <para>Every viewer keeps its own connection id, so several dashboards can be open at once.</para>
    /// <para>Runs every frame via <see cref="CycleAttribute"/> but throttles itself to one diff every 250 ms and does nothing while
    /// no dashboard is connected. When an <see cref="IVisibilityTracker"/> is registered, only player-owned objects (non-empty
    /// <c>ClientId</c>) and objects someone observes are included. Positions come from the physics body when present.</para>
    /// </remarks>
    [Portal("/ws/dashboard")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class DashboardPortal : Portal, OnConnectedAsync, OnDisconnectedAsync
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

        /// <summary>Created by the portal infrastructure through DI.</summary>
        /// <param name="gameWorldOrganizer">Source of the 3D worlds to stream.</param>
        /// <param name="gizmos">Gizmo registry whose changes are drained into each packet.</param>
        /// <param name="router">Router used to send packets to dashboard connections.</param>
        /// <param name="connectionManager">Used to list this portal's connections.</param>
        /// <param name="visibilityTracker">Optional; filters objects and adds visibility-radius gizmos.</param>
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

        /// <inheritdoc/>
        /// <remarks>Refreshes the cached list of dashboard connections.</remarks>
        public async Task OnConnectedAsync(
            string clientId,
            ConnectionManager connectionManager,
            AltruistConnection connection)
        {
            _connections = await _connectionManager.GetConnectionsForPortal(this);
        }

        /// <inheritdoc/>
        /// <remarks>Refreshes the cached list of dashboard connections.</remarks>
        public async Task OnDisconnectedAsync(string clientId, Exception? exception)
        {
            _connections = await _connectionManager.GetConnectionsForPortal(this);
        }

        /// <summary>
        /// Per-frame cycle: at most every 250 ms, diffs every world against the last sent state (position epsilon 1e-4,
        /// archetype, name) and sends one <see cref="DashboardWorldObjectStatePacket"/> per world that changed to every
        /// dashboard connection. Consumes <see cref="IDashboardGizmoRegistry.DrainChanges"/> for each world.
        /// </summary>
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
