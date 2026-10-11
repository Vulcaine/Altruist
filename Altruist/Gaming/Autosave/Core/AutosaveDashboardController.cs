/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.AspNetCore.Mvc;

namespace Altruist.Gaming.Autosave;

/// <summary>
/// <c>/dashboard/v1/autosave</c>: every autosave service with its dirty entities, write-ahead log, flushes and
/// failures; operators can flush them all now.
/// </summary>
[ApiController]
[Route("/dashboard/v1/autosave")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class AutosaveDashboardController : ControllerBase
{
    private readonly IAutosaveCoordinator _coordinator;

    /// <summary>Created by ASP.NET Core through DI.</summary>
    public AutosaveDashboardController(IAutosaveCoordinator coordinator) => _coordinator = coordinator;

    /// <summary><c>GET /dashboard/v1/autosave</c>: one report per registered service.</summary>
    [HttpGet]
    public ActionResult<IReadOnlyList<AutosaveServiceReport>> Get()
        => Ok((_coordinator as IAutosaveCoordinatorInspection)?.Services() ?? Array.Empty<AutosaveServiceReport>());

    /// <summary>
    /// <c>POST /dashboard/v1/autosave/flush</c>: writes every dirty entity of every service to its vault now (what
    /// fails stays dirty and is retried), then returns the reports.
    /// </summary>
    [HttpPost("flush")]
    public async Task<ActionResult<IReadOnlyList<AutosaveServiceReport>>> Flush()
    {
        await _coordinator.FlushAllAsync();
        return Get();
    }
}
