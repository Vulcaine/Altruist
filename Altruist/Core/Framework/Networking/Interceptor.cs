/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

namespace Altruist;

/// <summary>
/// Per-packet context handed to every <see cref="IInterceptor"/> before the gate handler runs. Call <see cref="Reject"/> to drop the packet.
/// </summary>
public class InterceptContext
{
    /// <summary>Event name (gate key) of the incoming packet.</summary>
    public string EventName { get; }

    /// <summary>Connection the packet arrived on ("" when unknown).</summary>
    public string ClientId { get; }

    /// <summary>Size of the raw payload in bytes (after the event-name prefix).</summary>
    public int PayloadLength { get; }

    /// <summary>Route (portal path) of the connection the packet arrived on, e.g. "/game" ("" when unknown).</summary>
    public string Route { get; } = "";

    /// <summary>True once an interceptor rejected the packet; the gate handler is then skipped.</summary>
    public bool Rejected { get; private set; }

    /// <summary>Creates a context without route information (<see cref="Route"/> is <c>""</c>).</summary>
    /// <param name="eventName">Event name of the packet.</param>
    /// <param name="clientId">Connection id ("" when unknown).</param>
    /// <param name="payloadLength">Raw payload size in bytes.</param>
    public InterceptContext(string eventName, string clientId = "", int payloadLength = 0)
    {
        EventName = eventName;
        ClientId = clientId;
        PayloadLength = payloadLength;
    }

    /// <summary>Creates a context including the connection's portal route.</summary>
    /// <param name="eventName">Event name of the packet.</param>
    /// <param name="clientId">Connection id.</param>
    /// <param name="payloadLength">Raw payload size in bytes.</param>
    /// <param name="route">Portal path of the connection; <c>null</c> becomes <c>""</c>.</param>
    public InterceptContext(string eventName, string clientId, int payloadLength, string route)
        : this(eventName, clientId, payloadLength)
    {
        Route = route ?? "";
    }

    /// <summary>Drops the packet: the gate handler does not run (e.g. rate limits, oversize frames).</summary>
    public void Reject() => Rejected = true;
}


/// <summary>
/// Runs before every gate handler (all interceptors complete before the handler starts). An
/// interceptor can veto the packet with <see cref="InterceptContext.Reject"/>. Packets for
/// unknown events are intercepted too (for rate limiting), with a null <c>eventData</c>.
/// Register one as <c>[Service(typeof(IInterceptor))]</c> and the connection manager picks it
/// up from DI; <see cref="IConnectionManager.AddInterceptor"/> still works for manual setup.
/// Use <see cref="InterceptContext.Route"/> to limit an interceptor to one portal.
/// </summary>
public interface IInterceptor
{
    /// <summary>Inspects an incoming packet before its gate handler runs; call <see cref="InterceptContext.Reject"/> to drop it.</summary>
    /// <param name="context">Packet context (event, client, size, route).</param>
    /// <param name="eventData">Decoded packet, or <c>null</c> when the event is unknown or could not be decoded.</param>
    /// <example><code>
    /// [Service(typeof(IInterceptor))]
    /// public sealed class MaxSizeInterceptor : IInterceptor
    /// {
    ///     public Task Intercept(InterceptContext ctx, IPacket eventData)
    ///     {
    ///         if (ctx.PayloadLength &gt; 4096) ctx.Reject();
    ///         return Task.CompletedTask;
    ///     }
    /// }
    /// </code></example>
    Task Intercept(InterceptContext context, IPacket eventData);
}

/// <summary>
/// An interceptor that keeps per-connection state (e.g. rate-limit buckets). The connection manager
/// calls <see cref="Forget"/> once the connection is gone, after the portals' <c>OnDisconnectedAsync</c>.
/// </summary>
public interface IConnectionStateInterceptor : IInterceptor
{
    /// <summary>Drops any state held for <paramref name="clientId"/>; called by the connection manager after the connection is closed.</summary>
    /// <param name="clientId">Connection id that disconnected.</param>
    void Forget(string clientId);
}


/// <summary>
/// Interceptor that forwards every decoded packet to <see cref="IRelayService.Relay(IPacket)"/> (packets with null data are skipped).
/// It never rejects. Not auto-registered; add it via <see cref="IConnectionManager.AddInterceptor"/> or your own DI registration.
/// </summary>
public class RelayInterceptor : IInterceptor
{
    private readonly IRelayService _relayService;

    /// <summary>Creates the interceptor.</summary>
    /// <param name="relayService">Relay that receives every packet.</param>
    public RelayInterceptor(IRelayService relayService)
    {
        _relayService = relayService;
    }

    /// <inheritdoc/>
    public async Task Intercept(InterceptContext context, IPacket eventData)
    {
        if (eventData is null)
            return;
        await _relayService.Relay(eventData);
        // // if (context.EventName == _relayService.RelayEvent)
        // {

        // }
    }
}
