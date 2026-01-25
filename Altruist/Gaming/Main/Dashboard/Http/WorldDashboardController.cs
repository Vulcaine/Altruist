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

namespace Altruist.Dashboard
{
    /// <summary>
    /// Dashboard controller exposing basic world & object info
    /// for the 3D environment, to be consumed by the Angular UI.
    /// </summary>
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

        /// <summary>
        /// List all loaded worlds.
        /// </summary>
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

        // --------------------------------------------------------------------
        // ✅ NEW: Non-stream snapshot endpoint (single JSON response)
        // --------------------------------------------------------------------

        /// <summary>
        /// Return ALL world objects grouped by partition as a single JSON snapshot.
        /// This is the non-stream version (good for polling / auto-refresh).
        /// </summary>
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

                // Optional: skip empty partitions to reduce payload size
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

        // --------------------------------------------------------------------
        // Existing Stream endpoint (NDJSON)
        // --------------------------------------------------------------------

        /// <summary>
        /// Stream all world objects, grouped by world partition, as NDJSON.
        /// Each line is:
        ///   { indexX, indexY, indexZ, objects: [ ... ] }
        /// </summary>
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

            // Get all partitions that intersect the whole world.
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

                // Skip empty partitions if you don't want them on the wire
                if (dto.Objects.Count == 0)
                    continue;

                await JsonSerializer.SerializeAsync(Response.Body, dto, _jsonOptions, ct);
                await Response.WriteAsync("\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }

        // --------------------------------------------------------------------
        // Shared DTO builder
        // --------------------------------------------------------------------

        private static WorldObjectDto BuildWorldObjectDto(IWorldObject3D o)
        {
            var wod = new WorldObjectDto
            {
                InstanceId = o.InstanceId,
                Archetype = o.ObjectArchetype ?? string.Empty,
                ZoneId = o.ZoneId,
                ClientId = o.ClientId,
                Expired = o.Expired,
                Transform = TransformDto.FromTransform(o.Transform),
                Colliders = new List<ColliderDto>()
            };

            foreach (var c in o.ColliderDescriptors ?? Enumerable.Empty<PhysxCollider3DDesc>())
            {
                wod.Colliders.Add(ColliderDto.FromCollider(c));
            }

            return wod;
        }
    }

    // ------------------------------------------------------------------------
    // DTOs
    // ------------------------------------------------------------------------

    /// <summary>
    /// Full snapshot response for non-stream endpoint.
    /// </summary>
    public sealed class WorldObjectsSnapshotDto
    {
        public int WorldIndex { get; set; }
        public string WorldName { get; set; } = string.Empty;
        public DateTime GeneratedAtUtc { get; set; }

        public List<WorldPartitionObjectsDto> Partitions { get; set; } = new();
    }

    /// <summary>
    /// DTO for streaming a single partition and its objects.
    /// One instance of this is written per NDJSON line.
    /// </summary>
    public sealed class WorldPartitionObjectsDto
    {
        public int IndexX { get; set; }
        public int IndexY { get; set; }
        public int IndexZ { get; set; }

        public List<WorldObjectDto> Objects { get; set; } = new();
    }
}
