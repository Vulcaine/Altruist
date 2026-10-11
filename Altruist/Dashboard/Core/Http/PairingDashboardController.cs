using Altruist.Networking;

using Microsoft.AspNetCore.Mvc;

namespace Altruist.Dashboard;

/// <summary>
/// <c>/dashboard/v1/pairing</c>: phone pairing for operators: the limits and every open session with its host and
/// companions (a session is a host screen and the phones driving it as remote pads).
/// </summary>
[ApiController]
[Route("/dashboard/v1/pairing")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class PairingDashboardController : ControllerBase
{
    private readonly PairingSessions? _sessions;

    /// <summary>Created by ASP.NET Core through DI.</summary>
    public PairingDashboardController(PairingSessions? sessions = null) => _sessions = sessions;

    /// <summary><c>GET /dashboard/v1/pairing</c>: <see cref="PairingReportDto"/>; <c>enabled</c> is false without <c>altruist:server:pairing</c>.</summary>
    [HttpGet]
    public ActionResult<PairingReportDto> Get()
        => Ok(_sessions is null
            ? new PairingReportDto(false, null, Array.Empty<PairingSessionInfo>())
            : new PairingReportDto(true, _sessions.Options, _sessions.Snapshot()));
}

/// <summary>Response of <c>GET /dashboard/v1/pairing</c>.</summary>
public sealed record PairingReportDto(bool Enabled, PairingOptions? Options, IReadOnlyList<PairingSessionInfo> Sessions);
