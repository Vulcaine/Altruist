/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.AspNetCore.Mvc;

using Altruist.Gaming.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Dashboard
{
    /// <summary>
    /// Dashboard controller exposing basic world & object info
    /// for the 3D environment, to be consumed by the Angular UI.
    /// </summary>
    [ApiController]
    [Route("/dashboard/v1/worlds")]
    // [ConditionalOnConfig("altruist:game")]
    // [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    // [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class WorldDashboardController : ControllerBase
    {
        private readonly IGameWorldOrganizer3D _worldOrganizer;

        public WorldDashboardController(IGameWorldOrganizer3D worldOrganizer)
        {
            _worldOrganizer = worldOrganizer;
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
        /// Get all world objects in the specified world.
        /// Intended for visualization / debugging in the dashboard.
        /// </summary>
        /// <param name="worldIndex">World index to query.</param>
        [HttpGet("{worldIndex:int}/objects")]
        public ActionResult<IEnumerable<WorldObjectDto>> GetWorldObjects(int worldIndex)
        {
            var world = _worldOrganizer.GetWorld(worldIndex);
            if (world is null)
                return NotFound(new { message = $"World {worldIndex} not found." });

            var objects = world.FindAllObjects<IWorldObject3D>()
                .Select(o => new WorldObjectDto
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
                    Transform = TransformDto.FromTransform(o.Transform)
                })
                .ToList();

            return Ok(objects);
        }
    }

    #region DTOs

    /// <summary>
    /// Lightweight summary of a world for dashboard display.
    /// </summary>
    public sealed class WorldSummaryDto
    {
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;

        public int PartitionCount { get; set; }
        public int ObjectCount { get; set; }
    }

    /// <summary>
    /// Flattened representation of a world object for dashboard use.
    /// </summary>
    public sealed class WorldObjectDto
    {
        public string InstanceId { get; set; } = string.Empty;
        public string Archetype { get; set; } = string.Empty;
        public string ZoneId { get; set; } = string.Empty;
        public string ClientId { get; set; } = string.Empty;

        public bool Expired { get; set; }

        public TransformDto Transform { get; set; } = default!;
    }

    public sealed class TransformDto
    {
        public Vector3Dto Position { get; set; } = default!;
        public Vector3Dto Size { get; set; } = default!;
        public Vector3Dto Scale { get; set; } = default!;

        public static TransformDto FromTransform(Transform3D t)
        {
            return new TransformDto
            {
                Position = new Vector3Dto
                {
                    X = t.Position.X,
                    Y = t.Position.Y,
                    Z = t.Position.Z
                },
                Size = new Vector3Dto
                {
                    X = t.Size.X,
                    Y = t.Size.Y,
                    Z = t.Size.Z
                },
                Scale = new Vector3Dto
                {
                    X = t.Scale.X,
                    Y = t.Scale.Y,
                    Z = t.Scale.Z
                }
            };
        }
    }

    public sealed class Vector3Dto
    {
        public float X { get; set; }
        public float Y { get; set; }
        public float Z { get; set; }
    }

    #endregion
}
