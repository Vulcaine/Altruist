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
/// Describes the running server's public address: a display name plus the scheme, host and port clients connect to.
/// Exposed as <see cref="IAltruistContext.ServerInfo"/> and in the health endpoint.
/// </summary>
/// <remarks>
/// Filled in by the framework at startup from the HTTP host/port settings (scheme <c>ws</c> when a WebSocket
/// transport is enabled, otherwise <c>http</c>); read it, don't construct it, unless you are writing tests.
/// </remarks>
public class ServerInfo
{
    /// <summary>Display name (the framework uses <c>"Altruist Server"</c>).</summary>
    public string Name { get; set; }
    /// <summary>URL scheme clients use, e.g. <c>"ws"</c> or <c>"http"</c>.</summary>
    public string Protocol { get; set; }
    /// <summary>Host name or IP the server listens on.</summary>
    public string Host { get; set; }
    /// <summary>TCP port of the HTTP/WebSocket listener.</summary>
    public int Port { get; set; }

    /// <summary>Creates a server address description.</summary>
    /// <param name="name">Display name.</param>
    /// <param name="protocol">URL scheme, e.g. <c>"ws"</c>.</param>
    /// <param name="host">Host name or IP.</param>
    /// <param name="port">Listener port.</param>
    public ServerInfo(string name, string protocol, string host, int port)
    {
        Name = name;
        Host = host;
        Port = port;
        Protocol = protocol;
    }
}
