/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using Microsoft.AspNetCore.Mvc;

namespace Altruist.Gaming.Rooms;

/// <summary>
/// <c>/dashboard/v1/rooms</c>: every room host of the game (found among the world steppers, or registered as
/// <see cref="IRoomHostInspection"/>) with its rooms, seats, players and module reports; operators can close a room.
/// </summary>
[ApiController]
[Route("/dashboard/v1/rooms")]
[ConditionalOnConfig("altruist:game")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class RoomsDashboardController : ControllerBase
{
    // Reports are taken at the next engine frame; a stopped engine never takes one.
    private static readonly TimeSpan FrameWait = TimeSpan.FromSeconds(3);

    private readonly IReadOnlyList<IRoomHostInspection> _hosts;

    /// <summary>Created by ASP.NET Core through DI.</summary>
    public RoomsDashboardController(IEnumerable<IRoomHostInspection> registered, IGameWorldOrganizer? worlds = null)
    {
        var steppers = (worlds as WorldCoordinator)?.Steppers.OfType<IRoomHostInspection>() ?? Enumerable.Empty<IRoomHostInspection>();
        _hosts = registered.Concat(steppers).Distinct(ReferenceEqualityComparer.Instance).Cast<IRoomHostInspection>().ToList();
    }

    /// <summary><c>GET /dashboard/v1/rooms</c>: one entry per host, by index: its report, or why there is none.</summary>
    [HttpGet]
    public async Task<ActionResult<IReadOnlyList<RoomHostEntryDto>>> Get(CancellationToken ct)
    {
        var entries = await Task.WhenAll(_hosts.Select(async (host, index) =>
        {
            try
            {
                return new RoomHostEntryDto(index, host.GameName, await host.ReportAsync(ct).WaitAsync(FrameWait, ct), null);
            }
            catch (TimeoutException)
            {
                return new RoomHostEntryDto(index, host.GameName, null, "No report within 3 s: the engine is not running frames.");
            }
        }));
        return Ok(entries);
    }

    /// <summary>
    /// <c>POST /dashboard/v1/rooms/{host}/{roomId}/close</c>: closes the room as a forced close (the game's
    /// <c>OnForcedClose</c>; the players go back to the menu or their lobby). 404 for an unknown host or room.
    /// </summary>
    [HttpPost("{host:int}/{roomId}/close")]
    public async Task<IActionResult> Close(int host, string roomId, CancellationToken ct)
    {
        if (host < 0 || host >= _hosts.Count)
            return NotFound(new { message = $"No room host {host}." });
        try
        {
            return await _hosts[host].CloseRoomAsync(roomId, ct).WaitAsync(FrameWait, ct)
                ? NoContent()
                : NotFound(new { message = $"No room {roomId}." });
        }
        catch (TimeoutException)
        {
            return StatusCode(503, new { message = "The engine is not running frames; nothing was closed." });
        }
    }
}

/// <summary>One room host in <c>GET /dashboard/v1/rooms</c>.</summary>
/// <param name="Index">Its index (for actions).</param>
/// <param name="Game">The game's name.</param>
/// <param name="Report">The report, or null when none came.</param>
/// <param name="Error">Why there is no report.</param>
public sealed record RoomHostEntryDto(int Index, string Game, RoomHostReport? Report, string? Error);
