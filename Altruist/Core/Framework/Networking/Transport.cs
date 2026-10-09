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

using Microsoft.AspNetCore.Builder;

namespace Altruist.Transport;

/// <summary>
/// A network transport (WebSocket, TCP, UDP) that accepts client connections and hands them to the
/// <see cref="Altruist.IConnectionManager"/>. Implementations register themselves with
/// <c>[Service(typeof(ITransport))]</c> gated by <c>altruist:server:transport:&lt;name&gt;:enabled</c>; the startup
/// pipeline iterates every registered transport.
/// </summary>
/// <remarks>
/// You normally do not call these members yourself: at startup, for <c>"websocket"</c> the framework calls
/// <see cref="UseTransportEndpoints(IApplicationBuilder, Type, string)"/> once per discovered portal and then
/// <see cref="RouteTraffic"/>; for any other transport type it calls
/// <see cref="UseTransportEndpoints(IApplicationBuilder, Type, string)"/> once with <c>typeof(IConnectionManager)</c> and path <c>"/game"</c>.
/// Implement this interface only to add a new transport kind.
/// </remarks>
public interface ITransport
{
    /// <summary>Identifies this transport type: "tcp", "udp", "websocket".</summary>
    string TransportType { get; }

    /// <summary>Installs the transport's request-handling middleware (WebSocket: accepts upgrades on registered routes). Called once after all endpoints are registered; socket transports may no-op.</summary>
    /// <param name="app">Application pipeline.</param>
    void RouteTraffic(IApplicationBuilder app);
    /// <summary>Registers an endpoint/route for <typeparamref name="TType"/> (e.g. a portal or shield type) at <paramref name="path"/>.</summary>
    /// <typeparam name="TType">Type that owns the endpoint; transports may read attributes (such as a shield) from it.</typeparam>
    /// <param name="app">Application pipeline.</param>
    /// <param name="path">Route path, e.g. <c>"/game"</c>.</param>
    void UseTransportEndpoints<TType>(IApplicationBuilder app, string path) where TType : class;
    /// <summary>Non-generic form of <see cref="UseTransportEndpoints{TType}(IApplicationBuilder, string)"/>; for socket transports this is where the listener is started.</summary>
    /// <param name="app">Application pipeline.</param>
    /// <param name="type">Type that owns the endpoint (portal type, or <c>IConnectionManager</c> for socket transports).</param>
    /// <param name="path">Route path.</param>
    void UseTransportEndpoints(IApplicationBuilder app, Type type, string path);
}
