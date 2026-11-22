using System.ComponentModel;
using System.Collections.Frozen;
using Altruist.CLI.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Altruist.CLI.Mcp;

/// <summary>
/// The running server, as MCP tools: what the dashboard API shows, read-only. The server must run with
/// <c>altruist:dashboard:enabled: true</c>; see <see cref="DashboardEndpoint"/> for where it is looked for.
/// </summary>
[McpServerToolType]
public sealed class RuntimeTools(DashboardClient dashboard)
{
    // Large enough for a world snapshot of a few hundred objects, small enough to leave the agent room to work.
    private const int MaxCharacters = 60_000;

    private static readonly FrozenDictionary<string, string> Views = new Dictionary<string, string>
    {
        ["summary"] = "dashboard/v1/summary",
        ["server"] = "dashboard/v1/server",
        ["architecture"] = "dashboard/v1/architecture",
        ["api"] = "dashboard/v1/lab/actions",
        ["sessions"] = "dashboard/v1/sessions",
        ["connections"] = "dashboard/v1/sessions/connections",
        ["rooms"] = "dashboard/v1/rooms",
        ["engine"] = "dashboard/v1/engine",
        ["gameplay"] = "dashboard/v1/gameplay",
        ["worlds"] = "dashboard/v1/worlds",
        ["vaults"] = "dashboard/v1/vaults",
        ["cache"] = "dashboard/v1/cache/info",
        ["autosave"] = "dashboard/v1/autosave",
        ["performance"] = "dashboard/v1/performance",
    }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    [McpServerTool(Name = "altruist_runtime", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("Reads one view of the running Altruist server (JSON). summary: every altruist config key (secrets as ***), registered services, engine settings. "
        + "server: this node's capacity and drain state and the fleet. architecture: how the servers, databases and caches connect. api: the HTTP routes. "
        + "sessions: rooms with their connections. connections: every connection. rooms: room hosts and their rooms. engine: tick rate and timing. "
        + "gameplay: AI agents and gameplay systems. worlds: the worlds (use altruist_runtime_world for their objects). vaults: the persisted types "
        + "(use altruist_runtime_vault for rows). cache: the cache provider. autosave: autosave services. performance: latency statistics.")]
    public Task<CallToolResult> ReadView(
        [Description("summary, server, architecture, api, sessions, connections, rooms, engine, gameplay, worlds, vaults, cache, autosave or performance.")] string view,
        CancellationToken ct) =>
        Views.TryGetValue(view, out var path)
            ? Read(path, ct)
            : Task.FromResult(Error($"There is no view \"{view}\". The views are: {string.Join(", ", Views.Keys)}."));

    [McpServerTool(Name = "altruist_runtime_world", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("The objects of one world of the running server, by partition, or one object with all its properties. World indexes come from altruist_runtime with view worlds.")]
    public Task<CallToolResult> ReadWorld(
        [Description("The world index.")] int worldIndex,
        [Description("An object's instance id, to read only that object.")] string? instanceId = null,
        CancellationToken ct = default) =>
        Read(instanceId is null
            ? $"dashboard/v1/worlds/{worldIndex}/objects"
            : $"dashboard/v1/worlds/{worldIndex}/objects/{Uri.EscapeDataString(instanceId)}", ct);

    [McpServerTool(Name = "altruist_runtime_vault", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("A page of the stored rows of one vault (persisted type) on the running server. Type keys come from altruist_runtime with view vaults.")]
    public Task<CallToolResult> ReadVault(
        [Description("The vault's type key.")] string typeKey,
        [Description("Rows to skip.")] int skip = 0,
        [Description("Rows to return, at most 500.")] int take = 50,
        CancellationToken ct = default) =>
        Read($"dashboard/v1/vaults/{Uri.EscapeDataString(typeKey)}/items?skip={skip}&take={take}", ct);

    [McpServerTool(Name = "altruist_runtime_traffic", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("The latest packets and connection events the running server recorded, newest last. Each has an id: pass the last one as sinceId to read only what came after it.")]
    public Task<CallToolResult> ReadTraffic(
        [Description("Return only events after this id.")] long? sinceId = null,
        [Description("Words to search for in the events, such as an event name or a connection id.")] string? query = null,
        [Description("Events to return, at most 300.")] int take = 100,
        CancellationToken ct = default)
    {
        var parameters = new List<string> { $"take={Math.Clamp(take, 1, 300)}" };
        if (sinceId is { } since)
            parameters.Add($"sinceId={since}");
        if (!string.IsNullOrWhiteSpace(query))
            parameters.Add($"query={Uri.EscapeDataString(query)}");
        return Read($"dashboard/v1/network/events?{string.Join('&', parameters)}", ct);
    }

    private async Task<CallToolResult> Read(string path, CancellationToken ct) =>
        await dashboard.GetAsync(path, ct) switch
        {
            DashboardReply.Ok ok => Text(Fit(NumericArrayElision.Apply(ok.Body))),
            DashboardReply.Failed failed => Error(failed.Message),
            var other => throw new InvalidOperationException($"Unknown dashboard reply {other.GetType().Name}."),
        };

    private static string Fit(string body) =>
        body.Length <= MaxCharacters
            ? body
            : body[..MaxCharacters] + $"\n[Cut at {MaxCharacters:N0} of {body.Length:N0} characters. Ask for one object, a smaller page or fewer events.]";

    private static CallToolResult Text(string text) => new() { Content = [new TextContentBlock { Text = text }] };

    private static CallToolResult Error(string message) => new() { Content = [new TextContentBlock { Text = message }], IsError = true };
}
