using System.ComponentModel;
using System.Text.Json;
using Altruist.CLI.Runtime;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Altruist.CLI.Mcp;

/// <summary>
/// Changes to the running server, as MCP tools. Each asks the person at the client to approve it (MCP elicitation)
/// and sends nothing without that approval; a client that cannot ask gets no change.
/// </summary>
[McpServerToolType]
public sealed class RuntimeChangeTools(RuntimeChanges changes)
{
    [McpServerTool(Name = "altruist_runtime_drain", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Drains the running Altruist server after the user approves: it takes no new work, and what still runs is stopped after the timeout. "
        + "It cannot be undone; the server is meant to stop afterwards. Use it to check that the game shuts down cleanly.")]
    public Task<CallToolResult> Drain(
        McpServer server,
        [Description("Seconds before running work is stopped; the server's drain timeout when left out.")] double? timeoutSeconds = null,
        CancellationToken ct = default) =>
        timeoutSeconds is <= 0
            ? Task.FromResult(Result(new ChangeOutcome.NotApplied("timeoutSeconds must be positive.")))
            : Apply(server, RuntimeChange.Drain(timeoutSeconds), ct);

    [McpServerTool(Name = "altruist_runtime_disconnect", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Disconnects one client from the running server after the user approves, and removes it from its rooms. "
        + "Connection ids come from altruist_runtime with view connections.")]
    public Task<CallToolResult> Disconnect(
        McpServer server,
        [Description("The connection id.")] string connectionId,
        CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(connectionId)
            ? Task.FromResult(Result(new ChangeOutcome.NotApplied("connectionId is empty.")))
            : Apply(server, RuntimeChange.Disconnect(connectionId), ct);

    [McpServerTool(Name = "altruist_runtime_close_room", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Closes a room on the running server after the user approves: every client in it is disconnected and the room is deleted. "
        + "Room ids come from altruist_runtime with view sessions.")]
    public Task<CallToolResult> CloseRoom(
        McpServer server,
        [Description("The room id.")] string roomId,
        CancellationToken ct = default) =>
        string.IsNullOrWhiteSpace(roomId)
            ? Task.FromResult(Result(new ChangeOutcome.NotApplied("roomId is empty.")))
            : Apply(server, RuntimeChange.CloseRoom(roomId), ct);

    [McpServerTool(Name = "altruist_runtime_set_config", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Sets live-editable config keys on the running server after the user approves, all or none. The change lives in memory "
        + "until the server restarts; to keep it, also edit config.yml. Which keys are live-editable: altruist_runtime with view summary (modifiable: true).")]
    public Task<CallToolResult> SetConfig(
        McpServer server,
        [Description("Each live-editable config key, as the summary view names it, with its new value as text.")] Dictionary<string, string> values,
        CancellationToken ct = default) =>
        values.Count == 0
            ? Task.FromResult(Result(new ChangeOutcome.NotApplied("values is empty.")))
            : Apply(server, RuntimeChange.SetConfig(values), ct);

    [McpServerTool(Name = "altruist_runtime_save_cache_entry", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Saves one cache entry on the running server after the user approves (to Redis and the local tier when the cache is Redis). "
        + "Types, groups and keys come from altruist_runtime with view cache-entries; the value must deserialize as that type.")]
    public Task<CallToolResult> SaveCacheEntry(
        McpServer server,
        [Description("The value's type, exactly as the cache-entries view gives it (assembly-qualified).")] string type,
        [Description("The entry key.")] string key,
        [Description("The new value as JSON.")] JsonElement value,
        [Description("The cache group; empty for the default group.")] string groupId = "",
        CancellationToken ct = default) =>
        Apply(server, RuntimeChange.SaveCacheEntry(new CacheEntryAddress(type, groupId, key), value), ct);

    [McpServerTool(Name = "altruist_runtime_remove_cache_entry", ReadOnly = false, Destructive = true, Idempotent = true, OpenWorld = false)]
    [Description("Removes one cache entry from the running server after the user approves (from Redis and the local tier when the cache is Redis). "
        + "Types, groups and keys come from altruist_runtime with view cache-entries.")]
    public Task<CallToolResult> RemoveCacheEntry(
        McpServer server,
        [Description("The value's type, exactly as the cache-entries view gives it (assembly-qualified).")] string type,
        [Description("The entry key.")] string key,
        [Description("The cache group; empty for the default group.")] string groupId = "",
        CancellationToken ct = default) =>
        Apply(server, RuntimeChange.RemoveCacheEntry(new CacheEntryAddress(type, groupId, key)), ct);

    private async Task<CallToolResult> Apply(McpServer server, RuntimeChange change, CancellationToken ct) =>
        Result(await changes.ApplyAsync(change, new ElicitationApproval(server), ct));

    private static CallToolResult Result(ChangeOutcome outcome) => outcome switch
    {
        ChangeOutcome.Applied applied => new() { Content = [new TextContentBlock { Text = applied.Message }] },
        ChangeOutcome.NotApplied notApplied => new() { Content = [new TextContentBlock { Text = notApplied.Message }], IsError = true },
        var other => throw new InvalidOperationException($"Unknown change outcome {other.GetType().Name}."),
    };
}
