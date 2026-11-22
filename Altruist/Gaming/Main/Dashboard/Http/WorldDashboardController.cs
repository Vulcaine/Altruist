/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Text.Json;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Altruist.Dashboard
{
    /// <summary>
    /// The world viewer's HTTP API under <c>/dashboard/v1/worlds</c>, for 2D and 3D worlds alike
    /// (<see cref="IDashboardWorldSource"/>): list the worlds, load one in full, inspect an object, and follow a world
    /// live as an NDJSON stream of <see cref="DashboardWorldDelta"/>s.
    /// </summary>
    /// <remarks>
    /// Enabled when an <c>altruist:game</c> config section exists, <c>altruist:dashboard:enabled = true</c> and the
    /// <c>Altruist.Dashboard</c> assembly is loaded. Without a world source (no 2D or 3D environment mode) the list is
    /// empty and every world is 404. Games publish debug visuals through <see cref="IDashboardGizmoRegistry"/>.
    /// </remarks>
    [ApiController]
    [Route("/dashboard/v1/worlds")]
    [ConditionalOnConfig("altruist:game")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class WorldDashboardController : ControllerBase
    {
        private static readonly TimeSpan Heartbeat = TimeSpan.FromSeconds(10);

        private readonly IDashboardWorldSource? _source;
        private readonly DashboardWorldFeed? _feed;
        private readonly IDashboardGizmoRegistry _gizmos;
        private readonly JsonSerializerOptions _json;
        private readonly DashboardWorldViewOptions _view;

        /// <summary>Created by ASP.NET Core through DI.</summary>
        public WorldDashboardController(
            IDashboardGizmoRegistry gizmos,
            JsonSerializerOptions json,
            DashboardWorldViewOptions view,
            IDashboardWorldSource? source = null,
            DashboardWorldFeed? feed = null)
        {
            _gizmos = gizmos;
            _json = json;
            _view = view;
            _source = source;
            _feed = feed;
        }

        /// <summary><c>GET /dashboard/v1/worlds</c>: every world (index, name, dimensions, partition and object counts), by index.</summary>
        [HttpGet]
        public ActionResult<IReadOnlyList<WorldSummaryDto>> GetWorlds() => Ok(_source?.Worlds() ?? Array.Empty<WorldSummaryDto>());

        /// <summary><c>GET /dashboard/v1/worlds/{worldIndex}/objects</c>: the whole world by partition, with render options and gizmos; 404 for an unknown world.</summary>
        [HttpGet("{worldIndex:int}/objects")]
        public ActionResult<WorldObjectsSnapshotDto> GetWorldObjectsSnapshot(int worldIndex)
        {
            if (_source is null || !_source.Exists(worldIndex))
                return NotFound(new { message = $"World {worldIndex} not found." });

            var summary = _source.Worlds().First(w => w.Index == worldIndex);
            return Ok(new WorldObjectsSnapshotDto
            {
                WorldIndex = worldIndex,
                WorldName = summary.Name,
                Dimensions = _source.Dimensions,
                GeneratedAtUtc = DateTime.UtcNow,
                RenderOptions = RenderOptions(),
                Partitions = _source.Partitions(worldIndex).ToList(),
                Gizmos = Gizmos(worldIndex).ToList(),
            });
        }

        /// <summary><c>GET /dashboard/v1/worlds/{worldIndex}/objects/{instanceId}</c>: one object in full (colliders included); 404 when it is not shown.</summary>
        [HttpGet("{worldIndex:int}/objects/{instanceId}")]
        public ActionResult<WorldObjectDto> GetWorldObject(int worldIndex, string instanceId)
            => _source?.Object(worldIndex, instanceId) is { } o ? Ok(o) : NotFound(new { message = $"Object {instanceId} not found in world {worldIndex}." });

        /// <summary><c>GET /dashboard/v1/worlds/{worldIndex}/gizmos</c>: the registry's gizmos of a world plus visibility radii; 404 for an unknown world.</summary>
        [HttpGet("{worldIndex:int}/gizmos")]
        public ActionResult<IEnumerable<DashboardGizmo>> GetWorldGizmos(int worldIndex)
            => _source is not null && _source.Exists(worldIndex) ? Ok(Gizmos(worldIndex).ToList()) : NotFound(new { message = $"World {worldIndex} not found." });

        /// <summary>
        /// <c>GET /dashboard/v1/worlds/{worldIndex}/objects/stream</c>: the snapshot's objects as <c>application/x-ndjson</c>,
        /// one <see cref="WorldPartitionObjectsDto"/> per line (flushed per partition), for very large worlds.
        /// </summary>
        [HttpGet("{worldIndex:int}/objects/stream")]
        public async Task StreamWorldObjects(int worldIndex, CancellationToken ct)
        {
            if (_source is null || !_source.Exists(worldIndex))
            {
                await NotFoundJson(worldIndex, ct);
                return;
            }

            Response.ContentType = "application/x-ndjson";
            foreach (var partition in _source.Partitions(worldIndex))
            {
                ct.ThrowIfCancellationRequested();
                await WriteLineAsync(partition, ct);
            }
        }

        /// <summary>
        /// <c>GET /dashboard/v1/worlds/{worldIndex}/live</c>: follows a world as <c>application/x-ndjson</c>. The first line
        /// is the whole world (<see cref="DashboardWorldDelta.Reset"/>), then one line per change (about four a second while
        /// things move); a line can be another reset when the viewer fell behind. An idle stream gets a
        /// <c>{"ping":"&lt;utc&gt;"}</c> line every 10 s. Ends when the client goes away. Needs the engine running.
        /// </summary>
        [HttpGet("{worldIndex:int}/live")]
        public async Task FollowWorld(int worldIndex, CancellationToken ct)
        {
            if (_source is null || _feed is null || !_source.Exists(worldIndex))
            {
                await NotFoundJson(worldIndex, ct);
                return;
            }

            Response.ContentType = "application/x-ndjson";
            Response.Headers.CacheControl = "no-cache";
            using var subscription = _feed.Subscribe(worldIndex);
            await Response.Body.FlushAsync(ct);
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idle.CancelAfter(Heartbeat);
                    try
                    {
                        if (!await subscription.Deltas.WaitToReadAsync(idle.Token))
                            return;
                        while (subscription.Deltas.TryRead(out var delta))
                            await WriteLineAsync(delta, ct);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        await WriteLineAsync(new { ping = DateTime.UtcNow }, ct);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // The viewer left.
            }
        }

        private IEnumerable<DashboardGizmo> Gizmos(int worldIndex)
            => _gizmos.GetSnapshot(worldIndex).Concat(_source!.SourceGizmos(worldIndex));

        private DashboardWorldRenderOptionsDto RenderOptions()
            => new() { RenderScale = _view.RenderScale, TerrainSampleStride = _view.TerrainSampleStride };

        private async Task WriteLineAsync<T>(T value, CancellationToken ct)
        {
            await JsonSerializer.SerializeAsync(Response.Body, value, _json, ct);
            await Response.WriteAsync("\n", ct);
            await Response.Body.FlushAsync(ct);
        }

        private async Task NotFoundJson(int worldIndex, CancellationToken ct)
        {
            Response.StatusCode = StatusCodes.Status404NotFound;
            await Response.WriteAsJsonAsync(new { message = $"World {worldIndex} not found." }, _json, ct);
        }
    }

    /// <summary>A whole world, returned by <see cref="WorldDashboardController.GetWorldObjectsSnapshot"/>.</summary>
    public sealed class WorldObjectsSnapshotDto
    {
        /// <summary>World index.</summary>
        public int WorldIndex { get; set; }
        /// <summary>World name (empty if unnamed).</summary>
        public string WorldName { get; set; } = string.Empty;
        /// <summary>2 or 3.</summary>
        public int Dimensions { get; set; } = 3;
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
        /// <summary>Partition grid index Z (0 in 2D worlds).</summary>
        public int IndexZ { get; set; }
        /// <summary>Objects in the partition.</summary>
        public List<WorldObjectDto> Objects { get; set; } = new();
    }
}
