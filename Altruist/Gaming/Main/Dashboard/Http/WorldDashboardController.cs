/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

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
        private readonly JsonSerializerOptions _jsonOptions;

        public WorldDashboardController(
            IGameWorldOrganizer3D worldOrganizer,
            JsonSerializerOptions jsonOptions)
        {
            _worldOrganizer = worldOrganizer;
            _jsonOptions = jsonOptions;
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
                    ObjectCount = w.FindAllObjects<IWorldObject3D>().Count()
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

            foreach (var partition in partitions)
            {
                var objs = partition.GetAllObjects<IWorldObject3D>();

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
                Partitions = partitionDtos
            };

            return Ok(snapshot);
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

            foreach (var partition in partitions)
            {
                ct.ThrowIfCancellationRequested();

                var objs = partition.GetAllObjects<IWorldObject3D>();

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

        private static WorldObjectDto BuildWorldObjectDto(IWorldObject3D o)
        {
            var effectiveTransform = o.Transform;

            if (o.Body is IPhysxBody3D body)
            {
                effectiveTransform = effectiveTransform
                    .WithPosition(Position3D.From(body.Position))
                    .WithRotation(Rotation3D.FromQuaternion(body.Rotation));
            }

            var wod = new WorldObjectDto
            {
                InstanceId = o.InstanceId,
                Archetype = o.ObjectArchetype ?? string.Empty,
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
                wod.Colliders.Add(BuildRuntimeColliderDto(c, effectiveTransform));
            }

            if (!anyRuntime)
            {
                foreach (var c in o.ColliderDescriptors ?? Enumerable.Empty<PhysxCollider3DDesc>())
                {
                    wod.Colliders.Add(ColliderDto.FromCollider(c));
                }
            }

            return wod;
        }

        private static ColliderDto BuildRuntimeColliderDto(IPhysxCollider3D c, Transform3D fallbackWorldTransform)
        {
            var t = c.Transform;
            if (t.Equals(Transform3D.Identity))
                t = fallbackWorldTransform;

            return new ColliderDto
            {
                Id = c.Id,
                Shape = c.Shape,
                Transform = TransformDto.FromTransform(t),
                IsTrigger = c.IsTrigger,
                Heightfield = c.Heightfield is null ? null : HeightfieldDto.FromHeightfield(c.Heightfield)
            };
        }
    }

    public sealed class WorldObjectsSnapshotDto
    {
        public int WorldIndex { get; set; }
        public string WorldName { get; set; } = string.Empty;
        public DateTime GeneratedAtUtc { get; set; }

        public List<WorldPartitionObjectsDto> Partitions { get; set; } = new();
    }

    public sealed class WorldPartitionObjectsDto
    {
        public int IndexX { get; set; }
        public int IndexY { get; set; }
        public int IndexZ { get; set; }

        public List<WorldObjectDto> Objects { get; set; } = new();
    }
}
