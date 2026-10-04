namespace Altruist.Client;

/// <summary>
/// One transport's send-side surface. Returned by <see cref="IAltruistClientRouter.Tcp"/>,
/// <see cref="IAltruistClientRouter.Udp"/>, and <see cref="IAltruistClientRouter.Ws"/>.
/// User code never injects the underlying transport class directly — only the
/// router exposes them, and only through this interface.
///
/// <para>Addressed sends (a-la <c>router.Tcp.Client("clientId").SendAsync(...)</c>)
/// are intentionally NOT in v1 — they require a server-side change to honour a
/// recipient field on the inbound frame, which we'd rather design from end to
/// end as a follow-up. For now, packet-level recipient fields and the server's
/// own routing logic do the same job.</para>
/// </summary>
public interface ITransportSender
{
    /// <summary>
    /// Send <paramref name="packet"/> on this transport, framed with
    /// <paramref name="gate"/> for the server's gate-name dispatcher. The packet
    /// is serialized via the configured <see cref="IClientCodec"/>; in practice
    /// it's expected to carry a wire-recognised <c>MessageCode</c> field, but
    /// the contract is intentionally loose so existing Unity / external packet
    /// types (which don't formally implement <c>IPacketBase</c>) can be sent
    /// without modification.
    /// </summary>
    Task SendAsync<T>(string gate, T packet, CancellationToken ct = default);
}
