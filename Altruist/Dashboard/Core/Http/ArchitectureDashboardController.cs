/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text.Json;

using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Altruist.Dashboard;

/// <summary>
/// The architecture of the fleet: every server with its listeners and portals, and the databases, cache and backplane
/// they use. Other servers are seen through the cards they publish to a shared backplane
/// (<see cref="ArchitectureCardPublisher"/>), servers of other clusters on it (independent fleets, channels) too; a
/// server of this fleet without the dashboard is known only from its heartbeat.
/// </summary>
[ApiController]
[Route("/dashboard/v1/architecture")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class ArchitectureDashboardController : ControllerBase
{
    private readonly ArchitectureCardBuilder _builder;
    private readonly IFleet _fleet;
    private readonly IFleetBackplane _backplane;
    private readonly ILogger _logger;

    /// <summary>Created by ASP.NET Core through DI.</summary>
    public ArchitectureDashboardController(ArchitectureCardBuilder builder, IFleet fleet, IFleetBackplane backplane, ILoggerFactory loggerFactory)
    {
        _builder = builder;
        _fleet = fleet;
        _backplane = backplane;
        _logger = loggerFactory.CreateLogger<ArchitectureDashboardController>();
    }

    /// <summary><c>GET /dashboard/v1/architecture</c>: <see cref="ArchitectureReportDto"/>.</summary>
    [HttpGet]
    public async Task<ActionResult<ArchitectureReportDto>> Get(CancellationToken ct)
    {
        var self = await _builder.BuildAsync();
        var others = _backplane.Shared ? await OtherCardsAsync(self.NodeId, ct) : Array.Empty<ArchitectureCardDto>();
        return Ok(new ArchitectureReportDto(
            self.NodeId,
            _fleet.Cluster,
            _builder.BackplaneKind,
            _backplane.Shared,
            _fleet.Nodes,
            others.Prepend(self).ToList()));
    }

    private async Task<IReadOnlyList<ArchitectureCardDto>> OtherCardsAsync(string self, CancellationToken ct)
    {
        var found = await _backplane.GetByPrefixAsync(ArchitectureCardPublisher.KeyPrefix, ct);
        var cards = new List<ArchitectureCardDto>();
        foreach (var (key, value) in found)
        {
            try
            {
                if (JsonSerializer.Deserialize<ArchitectureCardDto>(value, ArchitectureCardPublisher.Json) is { } card && card.NodeId != self)
                    cards.Add(card);
            }
            catch (JsonException ex)
            {
                // A card of another Altruist version: that server is drawn from its heartbeat alone.
                _logger.LogWarning(ex, "Ignored an unreadable architecture card at {Key}.", key);
            }
        }
        return cards.OrderBy(c => c.Cluster, StringComparer.Ordinal).ThenBy(c => c.NodeId, StringComparer.Ordinal).ToList();
    }
}
