/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.AspNetCore.Mvc;

using Altruist.Gaming.ThreeD;
using Altruist.Physx.ThreeD;
using System.Text.Json;
using Microsoft.AspNetCore.Http;

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

        /// <summary>
        /// Stream all world objects as NDJSON (one JSON object per line).
        /// Better for huge worlds than one giant array.
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

            // Enumerate objects and write each as its own JSON line
            foreach (var o in world.FindAllObjects<IWorldObject3D>())
            {
                ct.ThrowIfCancellationRequested();

                var dto = new WorldObjectDto
                {
                    InstanceId = o.InstanceId,
                    Archetype = o.Archetype ?? string.Empty,
                    ZoneId = (o as WorldObject3D)?.ZoneId
                             ?? (o as WorldObjectPrefab3D)?.ZoneId
                             ?? string.Empty,
                    ClientId = o.ClientId,
                    Expired = (o as WorldObject3D)?.Expired
                              ?? (o as WorldObjectPrefab3D)?.Expired
                              ?? false,
                    Transform = TransformDto.FromTransform(o.Transform),
                    Colliders = new List<ColliderDto>()
                };

                foreach (var c in o.ColliderDescriptors ?? Enumerable.Empty<PhysxCollider3DDesc>())
                {
                    dto.Colliders.Add(ColliderDto.FromCollider(c));
                }

                await JsonSerializer.SerializeAsync(Response.Body, dto, _jsonOptions, ct);
                await Response.WriteAsync("\n", ct);
                await Response.Body.FlushAsync(ct);
            }
        }
    }
}
