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
    /// <summary>
    /// Read-only HTTP API under <c>/dashboard/v1/worlds</c> used by the world dashboard viewer to list 3D worlds and load full
    /// snapshots (objects with transforms, colliders, terrain heightfields, gizmos); live deltas then come from
    /// <see cref="DashboardPortal"/>.
    /// </summary>
    /// <remarks>
    /// Enabled when an <c>altruist:game</c> config section exists, <c>altruist:dashboard:enabled = true</c> and the
    /// <c>Altruist.Dashboard</c> assembly is loaded. When an <see cref="IVisibilityTracker"/> is registered, non-terrain objects
    /// are included only if they are player-owned or observed. Heightfields are down-sampled by
    /// <see cref="DashboardWorldViewOptions.TerrainSampleStride"/>; heightfield objects that span several partitions are emitted once.
    /// Framework-internal: games publish debug visuals through <see cref="IDashboardGizmoRegistry"/> instead of calling this.
    /// </remarks>
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

        /// <summary>Created by ASP.NET Core through DI.</summary>
        /// <param name="worldOrganizer">Source of the 3D worlds.</param>
        /// <param name="gizmos">Gizmo registry included in snapshots.</param>
        /// <param name="jsonOptions">Serializer options used by the NDJSON stream.</param>
        /// <param name="viewOptions">Render options (scale, terrain stride).</param>
        /// <param name="visibilityTracker">Optional; filters objects and adds visibility-radius gizmos.</param>
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

        /// <summary><c>GET /dashboard/v1/worlds</c>: summary of every world (index, name, partition and object counts), ordered by index.</summary>
        /// <returns>200 with the world summaries.</returns>
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

        /// <summary><c>GET /dashboard/v1/worlds/{worldIndex}/objects</c>: full snapshot of a world grouped by partition, with render options and gizmos.</summary>
        /// <param name="worldIndex">World index.</param>
        /// <returns>200 with a <see cref="WorldObjectsSnapshotDto"/>, or 404 if the world does not exist.</returns>
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

        /// <summary><c>GET /dashboard/v1/worlds/{worldIndex}/gizmos</c>: current registry gizmos of a world plus visibility-radius gizmos.</summary>
        /// <param name="worldIndex">World index.</param>
        /// <returns>200 with the gizmos, or 404 if the world does not exist.</returns>
        [HttpGet("{worldIndex:int}/gizmos")]
        public ActionResult<IEnumerable<DashboardGizmo>> GetWorldGizmos(int worldIndex)
        {
            var world = _worldOrganizer.GetWorld(worldIndex);
            if (world is null)
                return NotFound(new { message = $"World {worldIndex} not found." });

            return Ok(BuildWorldGizmos(world));
        }

        /// <summary>
        /// <c>GET /dashboard/v1/worlds/{worldIndex}/objects/stream</c>: same objects as the snapshot endpoint, streamed as
        /// <c>application/x-ndjson</c> with one <see cref="WorldPartitionObjectsDto"/> per line (flushed per partition). Prefer it
        /// for very large worlds; it carries no gizmos or render options.
        /// </summary>
        /// <param name="worldIndex">World index.</param>
        /// <param name="ct">Request cancellation.</param>
        /// <returns>A task that completes when the stream ends; writes a 404 JSON body if the world does not exist.</returns>
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

    /// <summary>Full world snapshot returned by <see cref="WorldDashboardController.GetWorldObjectsSnapshot"/>.</summary>
    public sealed class WorldObjectsSnapshotDto
    {
        /// <summary>World index.</summary>
        public int WorldIndex { get; set; }
        /// <summary>World name (empty if unnamed).</summary>
        public string WorldName { get; set; } = string.Empty;
        /// <summary>Server time the snapshot was built (UTC).</summary>
        public DateTime GeneratedAtUtc { get; set; }

        /// <summary>Viewer render hints from <see cref="DashboardWorldViewOptions"/>.</summary>
        public DashboardWorldRenderOptionsDto RenderOptions { get; set; } = new();

        /// <summary>Non-empty partitions with their objects.</summary>
        public List<WorldPartitionObjectsDto> Partitions { get; set; } = new();

        /// <summary>Registry gizmos plus visibility-radius gizmos for this world.</summary>
        public List<DashboardGizmo> Gizmos { get; set; } = new();
    }

    /// <summary>Render hints sent with a snapshot; mirrors <see cref="DashboardWorldViewOptions"/>.</summary>
    public sealed class DashboardWorldRenderOptionsDto
    {
        /// <summary>See <see cref="DashboardWorldViewOptions.RenderScale"/>.</summary>
        public float RenderScale { get; set; } = 1f;
        /// <summary>See <see cref="DashboardWorldViewOptions.TerrainSampleStride"/>.</summary>
        public int TerrainSampleStride { get; set; } = 1;
    }

    /// <summary>One world partition and its objects, as returned in snapshots and NDJSON stream lines.</summary>
    public sealed class WorldPartitionObjectsDto
    {
        /// <summary>Partition grid index X.</summary>
        public int IndexX { get; set; }
        /// <summary>Partition grid index Y.</summary>
        public int IndexY { get; set; }
        /// <summary>Partition grid index Z.</summary>
        public int IndexZ { get; set; }

        /// <summary>Objects in the partition.</summary>
        public List<WorldObjectDto> Objects { get; set; } = new();
    }
}
