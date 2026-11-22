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
    /// <remarks>
    /// Wire framing per transport: TCP <c>[4-byte LE length][1-byte gate length][gate UTF-8][payload]</c>
    /// (thread-safe, serialized by a lock); UDP one datagram <c>[1-byte gate length][gate UTF-8][payload]</c>;
    /// WebSocket one message carrying the codec-encoded map <c>{ "event": gate, "payload": packet }</c>.
    /// </remarks>
    /// <typeparam name="T">Static packet type used for serialization.</typeparam>
    /// <param name="gate">Server gate name (UTF-8, at most 255 bytes on TCP/UDP).</param>
    /// <param name="packet">Packet to send.</param>
    /// <param name="ct">Cancellation token for the write.</param>
    /// <returns>A task that completes when the bytes are handed to the socket.</returns>
    /// <exception cref="InvalidOperationException">The transport is not connected (TCP / WS) or the gate name is longer than 255 bytes.</exception>
    Task SendAsync<T>(string gate, T packet, CancellationToken ct = default);
}
