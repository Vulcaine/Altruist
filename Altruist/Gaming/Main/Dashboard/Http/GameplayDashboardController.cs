/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using Altruist.Gaming;

using Microsoft.AspNetCore.Mvc;

namespace Altruist.Dashboard
{
    /// <summary>
    /// <c>/dashboard/v1/gameplay</c>: the game services for operators: the AI behaviors and every agent's state, and the
    /// zones with their players and spawns.
    /// </summary>
    [ApiController]
    [Route("/dashboard/v1/gameplay")]
    [ConditionalOnConfig("altruist:game")]
    [ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
    [ConditionalOnAssembly("Altruist.Dashboard")]
    public sealed class GameplayDashboardController : ControllerBase
    {
        private readonly IAIBehaviorService? _ai;
        private readonly IZoneManager? _zones;

        /// <summary>Created by ASP.NET Core through DI.</summary>
        public GameplayDashboardController(IAIBehaviorService? ai = null, IZoneManager? zones = null)
        {
            _ai = ai;
            _zones = zones;
        }

        /// <summary>
        /// <c>GET /dashboard/v1/gameplay</c>: <see cref="GameplayReportDto"/>. The agents are those of the last AI tick
        /// after the previous request, so the first request after a pause shows the copy from then (poll to follow).
        /// </summary>
        [HttpGet]
        public ActionResult<GameplayReportDto> Get()
        {
            var agents = (_ai as IAIInspection)?.Agents() ?? AIAgentsReport.None;
            var zones = _zones is null
                ? new List<ZoneReportDto>()
                : _zones.GetAllZoneNames()
                    .Select(name => _zones.GetZone(name))
                    .OfType<IManagedZone>()
                    .Select(z => new ZoneReportDto(z.Name, z.IsActive, z.PlayerCount, z.SpawnDefinitions.Count, z.SpawnDefinitions.Sum(s => s.Count), z.SpawnedInstanceIds.Count))
                    .OrderByDescending(z => z.Active)
                    .ThenBy(z => z.Name, StringComparer.Ordinal)
                    .ToList();
            return Ok(new GameplayReportDto(
                AIBehaviorDiscovery.Behaviors().Select(b => new AIBehaviorDto(b.Name, b.Type.FullName ?? b.Type.Name)).ToList(),
                agents,
                zones));
        }
    }

    /// <summary>Response of <c>GET /dashboard/v1/gameplay</c>.</summary>
    public sealed record GameplayReportDto(IReadOnlyList<AIBehaviorDto> Behaviors, AIAgentsReport Agents, IReadOnlyList<ZoneReportDto> Zones);

    /// <summary>A registered AI behavior.</summary>
    public sealed record AIBehaviorDto(string Name, string Type);

    /// <summary>One zone.</summary>
    /// <param name="Name">Zone name.</param>
    /// <param name="Active">Whether it is awake (players in it).</param>
    /// <param name="Players">Players in it.</param>
    /// <param name="SpawnPoints">Spawn definitions.</param>
    /// <param name="SpawnCapacity">Entities its definitions spawn.</param>
    /// <param name="Spawned">Entities spawned now.</param>
    public sealed record ZoneReportDto(string Name, bool Active, int Players, int SpawnPoints, int SpawnCapacity, int Spawned);
}
