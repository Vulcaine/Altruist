/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using Altruist.Gaming;
using Altruist.Gaming.TwoD;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Altruist.Dashboard
{
    /// <summary>
    /// The 2D worlds for the dashboard: positions are (x, y) with z = 0 and rotations are about z. Objects are shown with
    /// their physics body's pose and the body's colliders (each in body space: offset, rotation, shape size or vertices).
    /// With an <see cref="IVisibilityTracker"/>, objects are shown only if a client owns them or someone observes them.
    /// An object spanning several partitions is listed once, under the first.
    /// </summary>
    [Service(typeof(IDashboardWorldSource))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    [ConditionalOnConfig("altruist:game")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class DashboardWorldSource2D : IDashboardWorldSource
    {
        private readonly IGameWorldOrganizer2D _worlds;
        private readonly IVisibilityTracker? _visibility;

        /// <summary>Created by DI.</summary>
        public DashboardWorldSource2D(IGameWorldOrganizer2D worlds, IVisibilityTracker? visibility = null)
        {
            _worlds = worlds;
            _visibility = visibility;
        }

        /// <inheritdoc/>
        public int Dimensions => 2;

        /// <inheritdoc/>
        public IReadOnlyList<WorldSummaryDto> Worlds() => _worlds.GetAllWorlds()
            .Select(w => new WorldSummaryDto
            {
                Index = w.Index.Index,
                Name = w.Index.Name ?? string.Empty,
                Dimensions = 2,
                PartitionCount = PartitionsOf(w).Count,
                ObjectCount = w.GetAllObjects().Count(Shown),
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

            var emitted = new HashSet<string>(StringComparer.Ordinal);
            foreach (var partition in PartitionsOf(world))
            {
                var objects = partition.GetAllObjects<IWorldObject2D>()
                    .Where(o => Shown(o) && emitted.Add(o.InstanceId))
                    .Select(ToDto)
                    .ToList();
                if (objects.Count > 0)
                    yield return new WorldPartitionObjectsDto { IndexX = partition.Index.X, IndexY = partition.Index.Y, Objects = objects };
            }
        }

        /// <inheritdoc/>
        /// <remarks>Straight from the world, so an object outside every partition is shown too (as the live poses show it).</remarks>
        public IEnumerable<WorldObjectDto> Objects(int worldIndex)
            => _worlds.GetWorld(worldIndex)?.GetAllObjects().Where(Shown).Select(ToDto) ?? Enumerable.Empty<WorldObjectDto>();

        /// <inheritdoc/>
        public IEnumerable<DashboardObjectPoseDto> LiveObjects(int worldIndex)
        {
            if (_worlds.GetWorld(worldIndex) is not { } world)
                return Enumerable.Empty<DashboardObjectPoseDto>();
            return world.GetAllObjects().Where(Shown).Select(o =>
            {
                var (x, y, angle) = PoseOf(o);
                return new DashboardObjectPoseDto
                {
                    InstanceId = o.InstanceId,
                    Archetype = o.ObjectArchetype ?? string.Empty,
                    Name = DashboardWorldObjectNames.Resolve(o),
                    Position = new Vector3Dto { X = x, Y = y },
                    Rotation = QuaternionDto.AboutZ(angle),
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

            foreach (var observer in _visibility.GetObservers())
            {
                if (observer is not IWorldObject2D o || world.FindObject(o.InstanceId) is null)
                    continue;
                var (x, y, _) = PoseOf(o);
                yield return DashboardVisibilityGizmos.Of(worldIndex, o.InstanceId, new Vector3Dto { X = x, Y = y }, _visibility.ViewRange);
            }
        }

        private static List<WorldPartition2D> PartitionsOf(IGameWorldManager2D world)
            => world.FindPartitionsForBounds(float.MinValue, float.MinValue, float.MaxValue, float.MaxValue).OfType<WorldPartition2D>().ToList();

        private bool Shown(IWorldObject2D o)
            => _visibility is null || !string.IsNullOrEmpty(o.ClientId) || _visibility.GetObserversOf(o.InstanceId).Any();

        // The physics body's pose when the object has a body.
        private static (float X, float Y, float Angle) PoseOf(IWorldObject2D o)
            => o.Body is { } body
                ? (body.Position.X, body.Position.Y, body.RotationZ)
                : (o.Transform.Position.X, o.Transform.Position.Y, o.Transform.Rotation.Radians);

        private static WorldObjectDto ToDto(IWorldObject2D o)
        {
            var (x, y, angle) = PoseOf(o);
            var dto = new WorldObjectDto
            {
                InstanceId = o.InstanceId,
                Archetype = o.ObjectArchetype ?? string.Empty,
                Name = DashboardWorldObjectNames.Resolve(o),
                ZoneId = o.ZoneId,
                ClientId = o.ClientId,
                Expired = o.Expired,
                Transform = TransformDto.FromTransform(o.Transform
                    .WithPosition(Position2D.Of(x, y))
                    .WithRotation(new Rotation2D(angle))),
            };
            if (o.Body is { } body)
            {
                foreach (var collider in body.GetColliders())
                {
                    if (collider is IPhysxCollider2D c)
                        dto.Colliders.Add(ColliderDto.FromCollider(c));
                }
            }
            return dto;
        }
    }
}
