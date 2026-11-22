using System.Globalization;
using System.Text.Json;

namespace Altruist.CLI.Runtime;

/// <summary>One change to the running server through the dashboard API, with the question that asks for its approval.</summary>
/// <param name="Question">What the person is asked to approve, naming exactly what changes.</param>
/// <param name="Request">The dashboard request that makes the change.</param>
/// <param name="Done">What to tell the agent once the server accepted it.</param>
public sealed record RuntimeChange(string Question, DashboardRequest Request, string Done)
{
    /// <summary>Starts draining the server: no new work, running work finishes or is stopped after the timeout. It cannot be undone.</summary>
    public static RuntimeChange Drain(double? timeoutSeconds) =>
        new(
            timeoutSeconds is { } seconds
                ? $"Drain the running Altruist server? It takes no new work and stops what still runs after {seconds.ToString(CultureInfo.InvariantCulture)} seconds. A drain cannot be undone: the server is meant to stop afterwards."
                : "Drain the running Altruist server? It takes no new work and stops what still runs after its drain timeout. A drain cannot be undone: the server is meant to stop afterwards.",
            new DashboardRequest(HttpMethod.Post, timeoutSeconds is { } s
                ? $"dashboard/v1/server/drain?timeoutSeconds={s.ToString(CultureInfo.InvariantCulture)}"
                : "dashboard/v1/server/drain"),
            "The server is draining.");

    /// <summary>Disconnects one client and removes it from its rooms.</summary>
    public static RuntimeChange Disconnect(string connectionId) =>
        new(
            $"Disconnect client {connectionId} from the running Altruist server and remove it from its rooms?",
            new DashboardRequest(HttpMethod.Delete, $"dashboard/v1/sessions/connections/{Uri.EscapeDataString(connectionId)}"),
            $"Client {connectionId} is disconnected.");

    /// <summary>Disconnects every client in a room, then deletes the room.</summary>
    public static RuntimeChange CloseRoom(string roomId) =>
        new(
            $"Close room {roomId} on the running Altruist server? Every client in it is disconnected and the room is deleted.",
            new DashboardRequest(HttpMethod.Delete, $"dashboard/v1/sessions/rooms/{Uri.EscapeDataString(roomId)}"),
            $"Room {roomId} is closed.");

    /// <summary>Sets live-editable config keys in the server's memory; the server applies all of them or none.</summary>
    public static RuntimeChange SetConfig(IReadOnlyDictionary<string, string> values) =>
        new(
            "Set these config keys on the running Altruist server (in memory only; config files stay as they are)?\n"
                + string.Join("\n", values.Select(entry => $"{entry.Key} = {entry.Value}")),
            new DashboardRequest(HttpMethod.Post, "dashboard/v1/summary/config/update-batch",
                JsonSerializer.Serialize(values.Select(entry => new { key = entry.Key, value = entry.Value }))),
            $"{values.Count} config keys are set. They last until the server restarts.");

    /// <summary>Saves one cache entry (the remote store and the local tier when the cache is Redis).</summary>
    public static RuntimeChange SaveCacheEntry(CacheEntryAddress entry, JsonElement value) =>
        new(
            $"Save cache entry {entry} on the running Altruist server as:\n{value.GetRawText()}",
            new DashboardRequest(HttpMethod.Put, "dashboard/v1/cache/entry",
                JsonSerializer.Serialize(new { type = entry.Type, groupId = entry.GroupId, key = entry.Key, value })),
            $"Cache entry {entry} is saved.");

    /// <summary>Removes one cache entry (the remote store and the local tier when the cache is Redis).</summary>
    public static RuntimeChange RemoveCacheEntry(CacheEntryAddress entry) =>
        new(
            $"Remove cache entry {entry} from the running Altruist server?",
            new DashboardRequest(HttpMethod.Delete,
                $"dashboard/v1/cache/entry?Type={Uri.EscapeDataString(entry.Type)}&GroupId={Uri.EscapeDataString(entry.GroupId)}&Key={Uri.EscapeDataString(entry.Key)}"),
            $"Cache entry {entry} is removed.");
}

/// <summary>Where a cache entry lives.</summary>
/// <param name="Type">The value's assembly-qualified type name, as the cache-entries view lists it.</param>
/// <param name="GroupId">The cache group, empty for the default group.</param>
/// <param name="Key">The entry key.</param>
public sealed record CacheEntryAddress(string Type, string GroupId, string Key)
{
    // The assembly-qualified name is long; the person approving recognises the type by its name.
    public override string ToString() =>
        $"{Type.Split(',')[0].Split('.')[^1]} {(GroupId.Length == 0 ? "" : GroupId + "/")}{Key}";
}
