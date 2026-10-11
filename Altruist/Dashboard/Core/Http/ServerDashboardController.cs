using System.Diagnostics;

using Altruist.Http;
using Altruist.Security;

using Microsoft.AspNetCore.Mvc;

namespace Altruist.Dashboard;

/// <summary>
/// <c>/dashboard/v1/server</c>: this server and its fleet for operators: capacity and drain (with the drain action),
/// the other servers and the units they hold, the process's health, and the connection-ticket and rate-limit settings.
/// </summary>
[ApiController]
[Route("/dashboard/v1/server")]
[ConditionalOnConfig("altruist:dashboard:enabled", havingValue: "true")]
[ConditionalOnAssembly("Altruist.Dashboard")]
public sealed class ServerDashboardController : ControllerBase
{
    private readonly IServerNode _node;
    private readonly IFleet? _fleet;
    private readonly IConnectionTicketService? _tickets;
    private readonly IRequestRateLimiter? _httpLimiter;
    private readonly IEnumerable<IInterceptor> _interceptors;

    /// <summary>Created by ASP.NET Core through DI.</summary>
    public ServerDashboardController(
        IServerNode node,
        IEnumerable<IInterceptor> interceptors,
        IFleet? fleet = null,
        IConnectionTicketService? tickets = null,
        IRequestRateLimiter? httpLimiter = null)
    {
        _node = node;
        _interceptors = interceptors;
        _fleet = fleet;
        _tickets = tickets;
        _httpLimiter = httpLimiter;
    }

    /// <summary><c>GET /dashboard/v1/server</c>: the whole report (<see cref="ServerReportDto"/>).</summary>
    [HttpGet]
    public async Task<ActionResult<ServerReportDto>> Get(CancellationToken ct)
    {
        var fleetInspection = _fleet as IFleetInspection;
        return Ok(new ServerReportDto(
            Node(),
            _fleet is null ? null : new FleetReportDto(
                _fleet.Cluster,
                _fleet.Region,
                _fleet.Shared,
                fleetInspection?.BackplaneKind,
                fleetInspection?.Options.Heartbeat.TotalSeconds,
                fleetInspection?.Options.NodeTtl.TotalSeconds,
                _fleet.Self.NodeId,
                _fleet.Nodes),
            fleetInspection is null ? Array.Empty<FleetClaim>() : await fleetInspection.ClaimsAsync(ct),
            ProcessReportDto.Now(),
            _tickets is null ? null : new TicketSettingsDto(_tickets.Options.QueryParam, _tickets.Options.Lifetime.TotalSeconds,
                _tickets.Options.MaxPerPrincipal, _tickets.Options.SessionLifetime.TotalSeconds),
            _httpLimiter?.Options.Policies.Values
                .Select(p => new HttpRateLimitDto(p.Name, p.Permit, p.Window.TotalSeconds, p.Partition.ToString(), p.Path))
                .ToList() ?? new List<HttpRateLimitDto>(),
            _interceptors.OfType<RateLimitInterceptor>().FirstOrDefault()?.Options));
    }

    /// <summary>
    /// <c>POST /dashboard/v1/server/drain?timeoutSeconds=</c>: starts draining this server (no new work; running work
    /// finishes, then is stopped after the timeout) and returns at once with the node report. A drain cannot be undone:
    /// the server is meant to stop afterwards. A second call changes nothing.
    /// </summary>
    [HttpPost("drain")]
    public ActionResult<NodeReportDto> Drain([FromQuery] double? timeoutSeconds = null)
    {
        if (timeoutSeconds is <= 0)
            return BadRequest("timeoutSeconds must be positive.");
        _ = _node.DrainAsync(timeoutSeconds is { } s ? TimeSpan.FromSeconds(s) : null);
        return Ok(Node());
    }

    private NodeReportDto Node()
    {
        var capacity = _node.Capacity();
        var inspection = _node as IServerNodeInspection;
        return new NodeReportDto(
            capacity.NodeId,
            capacity.State.ToString(),
            capacity.Load,
            capacity.MaxLoad,
            capacity.Unlimited ? null : capacity.Utilization,
            capacity.Accepting,
            capacity.Kinds,
            _node.DrainTimeout.TotalSeconds,
            _node.IsDraining,
            inspection?.IsDrained ?? false,
            inspection?.WasForced ?? false,
            inspection?.DrainStartedUtc,
            inspection?.Participants() ?? Array.Empty<DrainParticipantStatus>());
    }
}

/// <summary>Response of <c>GET /dashboard/v1/server</c>.</summary>
/// <param name="Node">This server.</param>
/// <param name="Fleet">The fleet, when the server is in one.</param>
/// <param name="Claims">Units claimed in the fleet.</param>
/// <param name="Process">The process's health.</param>
/// <param name="Tickets">Connection-ticket settings, when tickets are available.</param>
/// <param name="HttpRateLimits">HTTP rate-limit policies.</param>
/// <param name="TransportRateLimit">Packet rate-limit settings, when the transport rate limit is on.</param>
public sealed record ServerReportDto(
    NodeReportDto Node,
    FleetReportDto? Fleet,
    IReadOnlyList<FleetClaim> Claims,
    ProcessReportDto Process,
    TicketSettingsDto? Tickets,
    IReadOnlyList<HttpRateLimitDto> HttpRateLimits,
    RateLimitOptions? TransportRateLimit);

/// <summary>This server's capacity and drain.</summary>
public sealed record NodeReportDto(
    string NodeId,
    string State,
    double Load,
    double MaxLoad,
    double? Utilization,
    bool Accepting,
    IReadOnlyList<CapacityByKind> Kinds,
    double DrainTimeoutSeconds,
    bool Draining,
    bool Drained,
    bool Forced,
    DateTime? DrainStartedUtc,
    IReadOnlyList<DrainParticipantStatus> Participants);

/// <summary>The fleet this server is in.</summary>
public sealed record FleetReportDto(
    string Cluster,
    string Region,
    bool Shared,
    string? Backplane,
    double? HeartbeatSeconds,
    double? NodeTtlSeconds,
    string SelfId,
    IReadOnlyList<FleetNodeInfo> Nodes);

/// <summary>Connection-ticket settings (<c>altruist:security:tickets</c>).</summary>
public sealed record TicketSettingsDto(string QueryParam, double LifetimeSeconds, int MaxPerPrincipal, double SessionLifetimeSeconds);

/// <summary>One HTTP rate-limit policy (<c>altruist:server:http:rate-limit</c>).</summary>
public sealed record HttpRateLimitDto(string Name, int Permit, double WindowSeconds, string Partition, string? Path);

/// <summary>The server process's health.</summary>
public sealed record ProcessReportDto(
    int ProcessId,
    string Machine,
    string Runtime,
    double UptimeSeconds,
    long WorkingSetBytes,
    long GcHeapBytes,
    long GcTotalAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double GcPauseTimePercent,
    double CpuPercent,
    int Processors,
    int Threads,
    int ThreadPoolThreads,
    long ThreadPoolQueue,
    long LockContentions)
{
    private static readonly object Gate = new();
    private static (DateTime At, TimeSpan Cpu) _lastCpu = (DateTime.UtcNow, Process.GetCurrentProcess().TotalProcessorTime);

    /// <summary>The process now; CPU is the share since the previous call (all cores = 100 %).</summary>
    public static ProcessReportDto Now()
    {
        using var process = Process.GetCurrentProcess();
        var now = DateTime.UtcNow;
        var cpu = process.TotalProcessorTime;
        double cpuPercent;
        lock (Gate)
        {
            var wall = (now - _lastCpu.At).TotalSeconds;
            cpuPercent = wall > 0 ? Math.Clamp((cpu - _lastCpu.Cpu).TotalSeconds / wall / Environment.ProcessorCount * 100, 0, 100) : 0;
            _lastCpu = (now, cpu);
        }
        var gc = GC.GetGCMemoryInfo();
        return new ProcessReportDto(
            Environment.ProcessId,
            Environment.MachineName,
            System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            (now - process.StartTime.ToUniversalTime()).TotalSeconds,
            process.WorkingSet64,
            GC.GetTotalMemory(false),
            GC.GetTotalAllocatedBytes(),
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2),
            gc.PauseTimePercentage,
            cpuPercent,
            Environment.ProcessorCount,
            process.Threads.Count,
            ThreadPool.ThreadCount,
            ThreadPool.PendingWorkItemCount,
            Monitor.LockContentionCount);
    }
}
