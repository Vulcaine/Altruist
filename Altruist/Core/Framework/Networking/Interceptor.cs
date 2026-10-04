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

public class InterceptContext
{
    public string EventName { get; }

    /// <summary>Connection the packet arrived on ("" when unknown).</summary>
    public string ClientId { get; }

    /// <summary>Size of the raw payload in bytes (after the event-name prefix).</summary>
    public int PayloadLength { get; }

    /// <summary>Route (portal path) of the connection the packet arrived on, e.g. "/game" ("" when unknown).</summary>
    public string Route { get; } = "";

    /// <summary>True once an interceptor rejected the packet; the gate handler is then skipped.</summary>
    public bool Rejected { get; private set; }

    public InterceptContext(string eventName, string clientId = "", int payloadLength = 0)
    {
        EventName = eventName;
        ClientId = clientId;
        PayloadLength = payloadLength;
    }

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
    Task Intercept(InterceptContext context, IPacket eventData);
}


public class RelayInterceptor : IInterceptor
{
    private readonly IRelayService _relayService;

    public RelayInterceptor(IRelayService relayService)
    {
        _relayService = relayService;
    }

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
