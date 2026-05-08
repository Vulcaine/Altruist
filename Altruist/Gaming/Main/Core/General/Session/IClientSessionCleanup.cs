namespace Altruist.Gaming;

/// <summary>
/// Marker for <c>[Service]</c>s that hold per-client state and want automatic
/// teardown when a client disconnects. Discovered at boot by
/// <see cref="SessionServiceConfiguration"/> and invoked from
/// <see cref="AltruistGameSessionPortal.OnDisconnectedAsync"/> after the
/// portal's own session-clear runs. One handler throwing does NOT stop the
/// rest — exceptions are logged and the next handler still runs.
///
/// <para>Use this for any per-clientId dictionary, observer dedup set, ack
/// table, ping timestamp, etc. — anywhere "I tracked something keyed by this
/// client and need to forget it now." Implementing this interface plus the
/// usual <c>[Service]</c> registration is enough; no double-tagging needed.</para>
///
/// <para><b>Note on naming:</b> this is intentionally NOT called
/// <c>ISessionService</c> because <c>Valeria.Core.Session.ISessionService</c>
/// already exists as a per-player session-state manager (works with
/// <c>PeerSession</c>). Both could be imported into the same file, so the
/// framework concept gets the more specific name.</para>
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
