/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Dashboard
{
    /// <summary>
    /// The 3D worlds for the dashboard. Objects are shown with their physics pose; when an <see cref="IVisibilityTracker"/>
    /// is registered, objects other than terrain are shown only if a client owns them or someone observes them.
    /// Heightfields are down-sampled by <see cref="DashboardWorldViewOptions.TerrainSampleStride"/>, and an object that
    /// spans several partitions (terrain) is listed once.
    /// </summary>
    [Service(typeof(IDashboardWorldSource))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "3D")]
    [ConditionalOnConfig("altruist:game")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class DashboardWorldSource3D : IDashboardWorldSource
    {
        private readonly IGameWorldOrganizer3D _worlds;
        private readonly DashboardWorldViewOptions _view;
        private readonly IVisibilityTracker? _visibility;

        /// <summary>Created by DI.</summary>
        public DashboardWorldSource3D(IGameWorldOrganizer3D worlds, DashboardWorldViewOptions view, IVisibilityTracker? visibility = null)
        {
            _worlds = worlds;
            _view = view;
            _visibility = visibility;
        }

        /// <inheritdoc/>
        public int Dimensions => 3;

        /// <inheritdoc/>
        public IReadOnlyList<WorldSummaryDto> Worlds() => _worlds.GetAllWorlds()
            .Select(w => new WorldSummaryDto
            {
                Index = w.Index.Index,
                Name = w.Index.Name ?? string.Empty,
                Dimensions = 3,
                PartitionCount = PartitionsOf(w).Count,
                ObjectCount = w.FindAllObjects<IWorldObject3D>().Count(Shown),
            })
            .OrderBy(w => w.Index)
            .ToList();

        /// <inheritdoc/>
        public bool Exists(int worldIndex) => _worlds.GetWorld(worldIndex) is not null;

        /// <inheritdoc/>
        public IEnumerable<WorldPartitionObjectsDto> Partitions(int worldIndex)
        {
            if (_worlds.GetWorld(worldIndex) is not { } world)
                yield break;

            var emittedLarge = new HashSet<string>(StringComparer.Ordinal);
            foreach (var partition in PartitionsOf(world))
            {
                var objects = partition.GetAllObjects<IWorldObject3D>()
                    .Where(Shown)
                    .Where(o => !IsLarge(o) || emittedLarge.Add(o.InstanceId))
                    .Select(ToDto)
                    .ToList();
                if (objects.Count > 0)
                    yield return new WorldPartitionObjectsDto { IndexX = partition.Index.X, IndexY = partition.Index.Y, IndexZ = partition.Index.Z, Objects = objects };
            }
        }

        /// <inheritdoc/>
        public IEnumerable<WorldObjectDto> Objects(int worldIndex) => Partitions(worldIndex).SelectMany(p => p.Objects);

        /// <inheritdoc/>
        public IEnumerable<DashboardObjectPoseDto> LiveObjects(int worldIndex)
        {
            if (_worlds.GetWorld(worldIndex) is not { } world)
                return Enumerable.Empty<DashboardObjectPoseDto>();
            // Terrain never moves: the snapshot has it, the feed skips it.
            return world.FindAllObjects<IWorldObject3D>()
                .Where(o => o is not Terrain && Shown(o))
                .Select(o =>
                {
                    var t = PoseOf(o);
                    return new DashboardObjectPoseDto
                    {
                        InstanceId = o.InstanceId,
                        Archetype = o.ObjectArchetype ?? string.Empty,
                        Name = DashboardWorldObjectNames.Resolve(o),
                        Position = new Vector3Dto { X = t.Position.X, Y = t.Position.Y, Z = t.Position.Z },
                        Rotation = QuaternionDto.From(t.Rotation.Value),
                    };
                });
        }

        /// <inheritdoc/>
        public WorldObjectDto? Object(int worldIndex, string instanceId)
            => _worlds.GetWorld(worldIndex)?.FindObject(instanceId) is { } o && Shown(o) ? ToDto(o) : null;

        /// <inheritdoc/>
        public IEnumerable<DashboardGizmo> SourceGizmos(int worldIndex)
        {
            if (_visibility is null || _worlds.GetWorld(worldIndex) is not { } world)
                yield break;

            var (_, lookup) = world.GetCachedSnapshot();
            foreach (var observer in _visibility.GetObservers())
            {
                if (observer is not IWorldObject3D o || !lookup.ContainsKey(o.InstanceId))
                    continue;
                var p = PoseOf(o).Position;
                yield return DashboardVisibilityGizmos.Of(worldIndex, o.InstanceId, new Vector3Dto { X = p.X, Y = p.Y, Z = p.Z }, _visibility.ViewRange);
            }
        }

        private static List<WorldPartitionManager3D> PartitionsOf(IGameWorldManager3D world)
            => world.FindPartitionsForPosition(0, 0, 0, float.MaxValue).OfType<WorldPartitionManager3D>().ToList();

        private bool Shown(IWorldObject3D o)
            => o is Terrain || _visibility is null || !string.IsNullOrEmpty(o.ClientId) || _visibility.GetObserversOf(o.InstanceId).Any();

        private static bool IsLarge(IWorldObject3D o)
            => o is Terrain || o.ColliderDescriptors.Any(c => c.Shape == PhysxColliderShape3D.Heightfield3D || c.Heightfield != null);

        // The physics body's pose when the object has a body (through the object's own sync when it defines one).
        private static Transform3D PoseOf(IWorldObject3D o)
        {
            if (o.Body is not IPhysxBody3D body)
                return o.Transform;
            if (o is IPhysicsTransformSync3D sync)
                return sync.GetWorldTransformFromPhysics(body);
            return o.Transform.WithPosition(Position3D.From(body.Position)).WithRotation(Rotation3D.FromQuaternion(body.Rotation));
        }

        private WorldObjectDto ToDto(IWorldObject3D o)
        {
            var pose = PoseOf(o);
            var dto = new WorldObjectDto
            {
                InstanceId = o.InstanceId,
                Archetype = o.ObjectArchetype ?? string.Empty,
                Name = DashboardWorldObjectNames.Resolve(o),
                ZoneId = o.ZoneId,
                ClientId = o.ClientId,
                Expired = o.Expired,
                Transform = TransformDto.FromTransform(pose),
            };

            var runtime = (o.Colliders ?? Enumerable.Empty<IPhysxCollider3D>()).ToList();
            if (runtime.Count > 0)
            {
                // A body collider follows the body; a heightfield keeps its own placement.
                var bodyPose = o.Body is IPhysxBody3D body
                    ? pose.WithPosition(Position3D.From(body.Position)).WithRotation(Rotation3D.FromQuaternion(body.Rotation))
                    : pose;
                foreach (var c in runtime)
                {
                    var t = c.Transform.Equals(Transform3D.Identity) || c.Shape != PhysxColliderShape3D.Heightfield3D ? bodyPose : c.Transform;
                    dto.Colliders.Add(new ColliderDto
                    {
                        Id = c.Id,
                        Shape = ColliderDto.ShapeName(c.Shape),
                        Transform = TransformDto.FromTransform(t),
                        IsTrigger = c.IsTrigger,
                        TransformSpace = "world",
                        Heightfield = c.Heightfield is null ? null : HeightfieldDto.FromHeightfield(c.Heightfield, _view.TerrainSampleStride),
                    });
                }
            }
            else
            {
                foreach (var c in o.ColliderDescriptors ?? Enumerable.Empty<PhysxCollider3DDesc>())
                    dto.Colliders.Add(ColliderDto.FromCollider(c, _view.TerrainSampleStride));
            }
            return dto;
        }
    }
}
