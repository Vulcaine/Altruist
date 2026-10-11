/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Networking;

/// <summary>
/// Device pairing over a portal: a host opens a session (<c>pair-host</c>) and shows its code;
/// companions join with it (<c>pair-join</c>); both send each other opaque payloads (<c>pair-send</c>),
/// which the server relays, and keep the connection alive with <c>pair-ping</c>. Use it to pair a phone with the game on a TV as a controller, a second
/// screen, or to signal a peer-to-peer (WebRTC) link between them that then bypasses the server.
/// </summary>
/// <remarks>
/// <para>
/// Abstract: derive with a route to turn it on, and configure <c>altruist:server:pairing</c> (the
/// limits; it registers the process-wide <see cref="PairingSessions"/>, portals are transient).
/// Companions need no account (they only know the code); keep the route behind the transport's
/// per-connection rate limit (<c>altruist:server:transport:rate-limit</c>). A disconnect leaves the
/// session: a host closes it.
/// The client side is <c>@altruist/client</c> (<c>PairingHost</c>, <c>PairingCompanion</c>).
/// </para>
/// <para>Sessions live in this process: the host and its companions must reach the same server.</para>
/// </remarks>
/// <example>
/// <code>
/// [Portal("/pair")]
/// public sealed class PairPortal : PairingPortal
/// {
///     public PairPortal(IAltruistRouter router, PairingSessions sessions) : base(router, sessions) { }
/// }
/// </code>
/// </example>
public abstract class PairingPortal : Portal, OnDisconnectedAsync
{
    private readonly IAltruistRouter _router;

    /// <summary>The sessions of this portal.</summary>
    protected PairingSessions Sessions { get; }

    /// <summary>Creates the portal over the process's sessions.</summary>
    /// <param name="router">Sends the replies.</param>
    /// <param name="sessions">The sessions (a singleton service when <c>altruist:server:pairing</c> is configured).</param>
    protected PairingPortal(IAltruistRouter router, PairingSessions sessions)
    {
        _router = router;
        Sessions = sessions;
    }

    /// <summary>Opens a session hosted by the sender: replies <see cref="PairingHostedPacket"/> or a refusal.</summary>
    [Gate("pair-host")]
    public Task OnHost(PairingHostPacket packet, string clientId)
    {
        var result = Sessions.Host(clientId);
        return result.Reject is { } reason
            ? Reject(clientId, reason)
            : _router.Client.SendAsync(clientId, new PairingHostedPacket { Code = result.Value!, MaxCompanions = Sessions.Options.MaxCompanions });
    }

    /// <summary>Joins the sender to a session: <see cref="PairingJoinedPacket"/> to it, <see cref="PairingPeerPacket"/> to the host.</summary>
    [Gate("pair-join")]
    public async Task OnJoin(PairingJoinPacket packet, string clientId)
    {
        var result = Sessions.Join(clientId, packet.Code);
        if (result.Reject is { } reason)
        {
            await Reject(clientId, reason).ConfigureAwait(false);
            return;
        }
        var join = result.Value;
        await _router.Client.SendAsync(clientId, new PairingJoinedPacket { Slot = join.Slot }).ConfigureAwait(false);
        await _router.Client.SendAsync(join.HostClientId, new PairingPeerPacket { Slot = join.Slot, Present = true }).ConfigureAwait(false);
    }

    /// <summary>Relays a payload to a peer of the sender's session as <see cref="PairingMessagePacket"/>.</summary>
    [Gate("pair-send")]
    public Task OnSend(PairingSendPacket packet, string clientId)
    {
        var payload = packet.Payload ?? Array.Empty<byte>();
        var result = Sessions.Route(clientId, packet.To, payload.Length);
        return result.Reject is { } reason
            ? Reject(clientId, reason)
            : _router.Client.SendAsync(result.Value.TargetClientId, new PairingMessagePacket { From = result.Value.FromSlot, Payload = payload });
    }

    /// <summary>Keep-alive: receiving it is enough (the connection is not idle).</summary>
    [Gate("pair-ping")]
    public Task OnPing(PairingPingPacket packet, string clientId) => Task.CompletedTask;

    /// <inheritdoc/>
    public async Task OnDisconnectedAsync(string clientId, Exception? exception)
    {
        // Every portal hears every disconnect: ids that never paired leave nobody to tell.
        foreach (var (peer, slot) in Sessions.Leave(clientId).Notify)
            await _router.Client.SendAsync(peer, new PairingPeerPacket { Slot = slot, Present = false }).ConfigureAwait(false);
    }

    private Task Reject(string clientId, PairingRejectReason reason) =>
        _router.Client.SendAsync(clientId, new PairingRejectedPacket { Reason = (int)reason });
}
