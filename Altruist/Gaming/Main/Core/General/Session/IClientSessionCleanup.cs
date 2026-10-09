namespace Altruist.Gaming;

/// <summary>
/// Marker for <c>[Service]</c>s that hold per-client state and want automatic
/// teardown when a client disconnects. Discovered at boot by
/// <see cref="ClientSessionCleanupConfiguration"/> and invoked from
/// <see cref="AltruistGameSessionPortal.OnDisconnectedAsync"/> after the
/// portal's own session-clear runs. One handler throwing does NOT stop the
/// rest — exceptions are logged and the next handler still runs.
///
/// <para>Use this for any per-clientId dictionary, observer dedup set, ack
/// table, ping timestamp, etc. — anywhere "I tracked something keyed by this
/// client and need to forget it now." Implementing this interface plus the
/// usual <c>[Service]</c> registration is enough; no double-tagging needed.</para>
///
/// <para>Only connections handled by an <see cref="AltruistGameSessionPortal"/> trigger it; a
/// game with its own portal (for example one driving a <c>RoomHost</c>) cleans up in its own
/// disconnect hook instead. Named <c>IClientSessionCleanup</c> (not <c>ISessionService</c>) so it does
/// not clash with application session types.</para>
/// <example>
/// <code>
/// [Service]
/// public sealed class PingTracker : IClientSessionCleanup
/// {
///     private readonly ConcurrentDictionary&lt;string, long&gt; _lastPing = new();
///     public Task Cleanup(string clientId) { _lastPing.TryRemove(clientId, out _); return Task.CompletedTask; }
/// }
/// </code>
/// </example>
/// </summary>
public interface IClientSessionCleanup
{
    /// <summary>
    /// Called once per client disconnect. Implementations should drop any
    /// per-client state keyed by <paramref name="clientId"/>. Idempotent —
    /// missing entries are a no-op, not an error.
    /// </summary>
    Task Cleanup(string clientId);
}
