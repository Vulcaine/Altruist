/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Dashboard
{
    [ApiController]
    [Route("/dashboard/v1/worlds")]
    [ConditionalOnConfig("altruist:game")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class WorldDashboardController : ControllerBase
    {
        private readonly IGameWorldOrganizer3D _worldOrganizer;
        private readonly IDashboardGizmoRegistry _gizmos;
        private readonly IVisibilityTracker? _visibilityTracker;
        private readonly JsonSerializerOptions _jsonOptions;
        private readonly DashboardWorldViewOptions _viewOptions;

        public WorldDashboardController(
            IGameWorldOrganizer3D worldOrganizer,
            IDashboardGizmoRegistry gizmos,
            JsonSerializerOptions jsonOptions,
            DashboardWorldViewOptions viewOptions,
            IVisibilityTracker? visibilityTracker = null)
        {
            _worldOrganizer = worldOrganizer;
            _gizmos = gizmos;
            _visibilityTracker = visibilityTracker;
            _jsonOptions = jsonOptions;
            _viewOptions = viewOptions;
        }

        [HttpGet]
        public ActionResult<IEnumerable<WorldSummaryDto>> GetWorlds()
        {
            var worlds = _worldOrganizer
                .GetAllWorlds()
                .Select(w => new WorldSummaryDto
                {
                    Index = w.Index.Index,
                    Name = w.Index.Name,
                    PartitionCount = w.FindPartitionsForPosition(0, 0, 0, float.MaxValue).Count(),
                    ObjectCount = w.FindAllObjects<IWorldObject3D>().Count(ShouldIncludeObject)
                })
                .OrderBy(w => w.Index)
                .ToList();

            return Ok(worlds);
        }

        [HttpGet("{worldIndex:int}/objects")]
        public ActionResult<WorldObjectsSnapshotDto> GetWorldObjectsSnapshot(int worldIndex)
        {
            var world = _worldOrganizer.GetWorld(worldIndex);
            if (world is null)
                return NotFound(new { message = $"World {worldIndex} not found." });

            var partitions = world
                .FindPartitionsForPosition(0, 0, 0, float.MaxValue)
                .OfType<WorldPartitionManager3D>()
                .ToList();

            var partitionDtos = new List<WorldPartitionObjectsDto>();
            var emittedLargeObjects = new HashSet<string>(StringComparer.Ordinal);

            foreach (var partition in partitions)
            {
                var objs = partition
                    .GetAllObjects<IWorldObject3D>()
                    .Where(ShouldIncludeObject)
                    .Where(o => !IsLargeSnapshotObject(o) || emittedLargeObjects.Add(o.InstanceId));

                var dto = new WorldPartitionObjectsDto
                {
                    IndexX = partition.Index.X,
                    IndexY = partition.Index.Y,
                    IndexZ = partition.Index.Z,
                    Objects = objs.Select(BuildWorldObjectDto).ToList()
                };

                if (dto.Objects.Count == 0)
                    continue;

                partitionDtos.Add(dto);
            }

            var snapshot = new WorldObjectsSnapshotDto
            {
                WorldIndex = world.Index.Index,
                WorldName = world.Index.Name ?? string.Empty,
                GeneratedAtUtc = DateTime.UtcNow,
                RenderOptions = BuildRenderOptions(),
                Partitions = partitionDtos,
                Gizmos = BuildWorldGizmos(world).ToList()
            };

            return Ok(snapshot);
        }

        [HttpGet("{worldIndex:int}/gizmos")]
        public ActionResult<IEnumerable<DashboardGizmo>> GetWorldGizmos(int worldIndex)
        {
            var world = _worldOrganizer.GetWorld(worldIndex);
            if (world is null)
                return NotFound(new { message = $"World {worldIndex} not found." });

            return Ok(BuildWorldGizmos(world));
        }

        [HttpGet("{worldIndex:int}/objects/stream")]
        public async Task StreamWorldObjects(int worldIndex, CancellationToken ct)
        {
            var world = _worldOrganizer.GetWorld(worldIndex);
            if (world is null)
            {
                Response.StatusCode = StatusCodes.Status404NotFound;
                await Response.WriteAsJsonAsync(
                    new { message = $"World {worldIndex} not found." }, _jsonOptions, ct);
                return;
            }

            Response.StatusCode = StatusCodes.Status200OK;
            Response.ContentType = "application/x-ndjson";

            var partitions = world
                .FindPartitionsForPosition(0, 0, 0, float.MaxValue)
                .OfType<WorldPartitionManager3D>()
                .ToList();
            var emittedLargeObjects = new HashSet<string>(StringComparer.Ordinal);

            foreach (var partition in partitions)
            {
                ct.ThrowIfCancellationRequested();

                var objs = partition
                    .GetAllObjects<IWorldObject3D>()
                    .Where(ShouldIncludeObject)
                    .Where(o => !IsLargeSnapshotObject(o) || emittedLargeObjects.Add(o.InstanceId));

                var dto = new WorldPartitionObjectsDto
                {
                    IndexX = partition.Index.X,
                    IndexY = partition.Index.Y,
                    IndexZ = partition.Index.Z,
                    Objects = objs.Select(BuildWorldObjectDto).ToList()
                };

                if (dto.Objects.Count == 0)
                    continue;

                await JsonSerializer.SerializeAsync(Response.Body, dto, _jsonOptions, ct);
                await Response.WriteAsync("\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }

        private WorldObjectDto BuildWorldObjectDto(IWorldObject3D o)
        {
            var effectiveTransform = GetWorldObjectTransform(o);
            var colliderFallbackTransform = GetColliderFallbackTransform(o, effectiveTransform);


            var wod = new WorldObjectDto
            {
                InstanceId = o.InstanceId,
                Archetype = o.ObjectArchetype ?? string.Empty,
                Name = DashboardWorldObjectNames.Resolve(o),
                ZoneId = o.ZoneId,
                ClientId = o.ClientId,
                Expired = o.Expired,
                Transform = TransformDto.FromTransform(effectiveTransform),
                Colliders = new List<ColliderDto>()
            };

            var runtimeColliders = o.Colliders ?? Enumerable.Empty<IPhysxCollider3D>();
            bool anyRuntime = false;

            foreach (var c in runtimeColliders)
            {
                anyRuntime = true;
                wod.Colliders.Add(BuildRuntimeColliderDto(c, colliderFallbackTransform));
            }

            if (!anyRuntime)
            {
                foreach (var c in o.ColliderDescriptors ?? Enumerable.Empty<PhysxCollider3DDesc>())
                {
                    wod.Colliders.Add(ColliderDto.FromCollider(c, _viewOptions.TerrainSampleStride));
                }
            }

            return wod;
        }

        private static Transform3D GetWorldObjectTransform(IWorldObject3D o)
        {
            if (o.Body is not IPhysxBody3D body)
                return o.Transform;

            if (o is IPhysicsTransformSync3D transformSync)
                return transformSync.GetWorldTransformFromPhysics(body);

            return o.Transform
                .WithPosition(Position3D.From(body.Position))
                .WithRotation(Rotation3D.FromQuaternion(body.Rotation));
        }

        private static Transform3D GetColliderFallbackTransform(IWorldObject3D o, Transform3D objectTransform)
        {
            if (o.Body is not IPhysxBody3D body)
                return objectTransform;

            return objectTransform
                .WithPosition(Position3D.From(body.Position))
                .WithRotation(Rotation3D.FromQuaternion(body.Rotation));
        }

        private static bool IsLargeSnapshotObject(IWorldObject3D o)
            => o is Terrain
                || o.ColliderDescriptors.Any(c => c.Shape == PhysxColliderShape3D.Heightfield3D || c.Heightfield != null);

        private bool ShouldIncludeObject(IWorldObject3D o)
        {
            if (o is Terrain)
                return true;

            if (_visibilityTracker is null)
                return true;

            if (!string.IsNullOrEmpty(o.ClientId))
                return true;

            return _visibilityTracker.GetObserversOf(o.InstanceId).Any();
        }

        private IEnumerable<DashboardGizmo> BuildWorldGizmos(IGameWorldManager3D world)
            => _gizmos.GetSnapshot(world.Index.Index).Concat(BuildVisibilityRadiusGizmos(world));

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

                var p = observer3D.Transform.Position;
                yield return new DashboardGizmo
                {
                    Id = $"altruist:visibility:observer:{observer3D.InstanceId}:radius",
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

        private DashboardWorldRenderOptionsDto BuildRenderOptions()
            => new()
            {
                RenderScale = _viewOptions.RenderScale,
                TerrainSampleStride = _viewOptions.TerrainSampleStride
            };

        private ColliderDto BuildRuntimeColliderDto(IPhysxCollider3D c, Transform3D fallbackWorldTransform)
        {
            var t = c.Transform;
            if (t.Equals(Transform3D.Identity) || c.Shape != PhysxColliderShape3D.Heightfield3D)
                t = fallbackWorldTransform;

            return new ColliderDto
            {
                Id = c.Id,
                Shape = c.Shape,
                Transform = TransformDto.FromTransform(t),
                IsTrigger = c.IsTrigger,
                TransformSpace = "world",
                Heightfield = c.Heightfield is null ? null : HeightfieldDto.FromHeightfield(c.Heightfield, _viewOptions.TerrainSampleStride)
            };
        }
    }

    public sealed class WorldObjectsSnapshotDto
    {
        public int WorldIndex { get; set; }
        public string WorldName { get; set; } = string.Empty;
        public DateTime GeneratedAtUtc { get; set; }

        public DashboardWorldRenderOptionsDto RenderOptions { get; set; } = new();

        public List<WorldPartitionObjectsDto> Partitions { get; set; } = new();

        public List<DashboardGizmo> Gizmos { get; set; } = new();
    }

    public sealed class DashboardWorldRenderOptionsDto
    {
        public float RenderScale { get; set; } = 1f;
        public int TerrainSampleStride { get; set; } = 1;
    }

    public sealed class WorldPartitionObjectsDto
    {
        public int IndexX { get; set; }
        public int IndexY { get; set; }
        public int IndexZ { get; set; }

        public List<WorldObjectDto> Objects { get; set; } = new();
    }
}
