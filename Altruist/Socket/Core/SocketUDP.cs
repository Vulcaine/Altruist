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

using System.Data.HashFunction.MurmurHash;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

using Altruist.Contracts;
using Altruist.Security;
using Altruist.Transport;

using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Socket;

/// <summary>
/// Experimental UDP transport (<c>altruist:server:transport:udp:enabled: true</c>), listening on
/// <c>altruist:server:transport:udp:port</c> (13001) with route <c>altruist:server:transport:udp:event</c> (<c>/game</c>).
/// Client ids are a Murmur3 hash of the sender's <c>ip:port</c>.
/// </summary>
/// <remarks>
/// Not production-ready: every datagram from an endpoint without a live stored connection starts a new
/// <see cref="IConnectionManager.HandleConnection"/> session whose read loop receives on the shared socket, and when the
/// connection manager type carries a <c>ShieldAttribute</c> every client is rejected (token extraction is not implemented).
/// Prefer the WebSocket or <see cref="TcpTransport"/> transports.
/// </remarks>
[Service(typeof(ITransport))]
[ConditionalOnConfig("altruist:server:transport:udp:enabled", havingValue: "true")]
public sealed class UdpTransport : ITransport, IDisposable
{
    private readonly int _port;
    private UdpClient? _udpClient;
    private readonly string _endpoint;
    private readonly ICodec _codec;

    private readonly IConnectionStore _store;

    private static readonly IMurmurHash3 _hasher = MurmurHash3Factory.Instance.Create();

    /// <summary>Always <c>"udp"</c>.</summary>
    public string TransportType => "udp";

    /// <summary>DI constructor.</summary>
    /// <param name="store">Connection store used to look up existing endpoints.</param>
    /// <param name="codec">Codec used to decode the handshake.</param>
    /// <param name="event">Route assigned to UDP connections (<c>udp:event</c>).</param>
    /// <param name="port">Listen port (<c>udp:port</c>, default 13001).</param>
    public UdpTransport(
        IConnectionStore store,
        ICodec codec,
        [AppConfigValue("altruist:server:transport:udp:event", "/game")] string @event,
        [AppConfigValue("altruist:server:transport:udp:port", "13001")] int port = 13001)
    {
        _port = port;
        _codec = codec;
        _endpoint = @event;
        _store = store;
    }

    /// <summary>Starts the UDP receive loop using the DI <see cref="IConnectionManager"/>; <paramref name="path"/> is ignored.</summary>
    /// <typeparam name="TType">Ignored.</typeparam>
    /// <param name="app">Application (service provider source).</param>
    /// <param name="path">Ignored.</param>
    public void UseTransportEndpoints<TType>(IApplicationBuilder app, string path) where TType : class
    {
        StartUdpServer(app.ApplicationServices.GetRequiredService<IConnectionManager>(), app.ApplicationServices);
    }

    /// <summary>Starts the UDP receive loop using the service of <paramref name="type"/> as the connection manager; <paramref name="path"/> is ignored.</summary>
    /// <param name="app">Application (service provider source).</param>
    /// <param name="type">Service type resolving to an <see cref="IConnectionManager"/>.</param>
    /// <param name="path">Ignored.</param>
    public void UseTransportEndpoints(IApplicationBuilder app, Type type, string path)
    {
        StartUdpServer((app.ApplicationServices.GetRequiredService(type) as IConnectionManager)!, app.ApplicationServices);
    }

    private void StartUdpServer(IConnectionManager connectionManager, IServiceProvider serviceProvider)
    {
        _udpClient = new UdpClient(_port);

        Task.Run(async () =>
        {
            while (true)
            {
                var result = await _udpClient.ReceiveAsync();
                _ = HandleClient(result, connectionManager, serviceProvider);
            }
        });
    }

    string ComputeMurmurHash(string input)
    {
        byte[] hashBytes = _hasher.ComputeHash(Encoding.UTF8.GetBytes(input)).Hash;
        return Convert.ToHexString(hashBytes);
    }

    private async Task HandleClient(UdpReceiveResult udpResult, IConnectionManager connectionManager, IServiceProvider serviceProvider)
    {
        var buffer = udpResult.Buffer;
        var clientIp = udpResult.RemoteEndPoint;
        var clientConnectionId = $"{clientIp.Address}:{clientIp.Port}";
        var clientId = ComputeMurmurHash(clientConnectionId);
        var existingConn = await _store.GetConnectionAsync(clientId);
        AuthDetails? authDetails = null;

        // Try to authenticate the client
        if (existingConn == null || !existingConn.IsConnected)
        {
            var errorMessage = Encoding.UTF8.GetBytes("Authentication failed.");
            var shieldAttribute = connectionManager.GetType().GetCustomAttribute<ShieldAttribute>();

            var handshakeMessage = _codec.Decoder.Decode<HandshakeRequestPacket>(buffer);

            // TODO: get token from handshakeMessage
            var authContext = new SocketAuthContext
            {
                Token = "",
                ClientId = clientId,
                ClientIp = clientIp.Address,
                ConnectionTimestamp = DateTime.UtcNow
            };

            if (shieldAttribute != null)
            {
                if (string.IsNullOrEmpty(""))
                {
                    await _udpClient!.SendAsync(errorMessage, errorMessage.Length, clientIp);
                    return;
                }

                authDetails = await shieldAttribute.AuthenticateNonHttpAsync(serviceProvider, authContext);

                if (authDetails == null)
                {
                    await _udpClient!.SendAsync(errorMessage, errorMessage.Length, clientIp);
                    return;
                }
            }
        }

        var connection = new CachedUdpConnection(new UdpConnection(_udpClient!, clientId, authDetails, clientIp));
        await connectionManager.HandleConnection(connection, _endpoint, clientId);
    }

    /// <summary>No-op (UDP does not use the HTTP pipeline).</summary>
    /// <param name="app">Unused.</param>
    public void RouteTraffic(IApplicationBuilder app) { }

    /// <summary>Disposes the UDP socket.</summary>
    public void Dispose()
    {
        _udpClient?.Dispose();
        _udpClient = null;
    }
}

/// <summary>Store-friendly wrapper around a <see cref="UdpConnection"/> that forwards I/O. Created by <see cref="UdpTransport"/>.</summary>
public sealed class CachedUdpConnection : AltruistConnection
{
    [JsonIgnore]
    private UdpConnection? _udpConnection;

    /// <summary>Transport type tag (hides the base <c>Type</c>).</summary>
    public new string Type { get; } = "udp";

    /// <summary>Wraps a UDP endpoint connection.</summary>
    /// <param name="udpConnection">Underlying connection.</param>
    public CachedUdpConnection(UdpConnection udpConnection)
    {
        _udpConnection = udpConnection;
        ConnectionId = udpConnection.ConnectionId;
        AuthDetails = udpConnection.AuthDetails;
        LastActivity = udpConnection.LastActivity;
    }

    /// <summary>Creates a detached copy (metadata only): sends are dropped, receives return empty.</summary>
    /// <param name="connection">Connection to copy id, auth details and last activity from.</param>
    public CachedUdpConnection(AltruistConnection connection)
    {
        ConnectionId = connection.ConnectionId;
        AuthDetails = connection.AuthDetails;
        LastActivity = connection.LastActivity;
    }

    /// <summary>
    /// True when <c>LastActivity</c> is within 30 minutes. Hides (does not override) the base property, so code holding an
    /// <see cref="AltruistConnection"/> reference sees the base value instead.
    /// </summary>
    [JsonIgnore]
    public new bool IsConnected => DateTime.UtcNow - LastActivity < TimeSpan.FromMinutes(30);

    /// <inheritdoc/>
    public override async Task SendAsync(byte[] data)
    {
        if (_udpConnection != null)
        {
            await _udpConnection.SendAsync(data);
        }
    }

    /// <inheritdoc/>
    public override async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_udpConnection != null)
        {
            return await _udpConnection.ReceiveAsync(cancellationToken);
        }
        else
        {
            return Array.Empty<byte>();
        }
    }

    /// <inheritdoc/>
    public override Task CloseAsync()
    {
        if (_udpConnection != null)
        {
            return _udpConnection.CloseAsync();
        }
        else
        {
            return Task.CompletedTask;
        }
    }
}

/// <summary>One remote UDP endpoint on the shared server socket. UDP is connectionless: close is a no-op.</summary>
public sealed class UdpConnection : AltruistConnection
{
    [JsonIgnore]
    private readonly UdpClient? _client;

    [JsonIgnore]
    private readonly IPEndPoint? _remoteEndPoint;

    /// <summary>True when <c>LastActivity</c> is within 30 minutes (hides, does not override, the base property).</summary>
    [JsonIgnore]
    public new bool IsConnected => DateTime.UtcNow - LastActivity < TimeSpan.FromMinutes(30);

    /// <summary>Transport type tag (hides the base <c>Type</c>).</summary>
    public new string Type { get; } = "udp";

    /// <summary>Creates an endpoint connection.</summary>
    /// <param name="client">Shared server socket.</param>
    /// <param name="connectionId">Connection id.</param>
    /// <param name="authDetails">Authentication result, if any.</param>
    /// <param name="remoteEndPoint">Remote endpoint datagrams are sent to.</param>
    public UdpConnection(UdpClient client, string connectionId, AuthDetails? authDetails, IPEndPoint remoteEndPoint)
    {
        _client = client;
        ConnectionId = connectionId;
        AuthDetails = authDetails;
        _remoteEndPoint = remoteEndPoint;
    }

    /// <summary>Sends one datagram to the remote endpoint.</summary>
    /// <param name="data">Datagram bytes.</param>
    /// <exception cref="InvalidOperationException">No socket.</exception>
    public override async Task SendAsync(byte[] data)
    {
        if (IsConnected && _client != null)
        {
            await _client.SendAsync(data, data.Length, _remoteEndPoint);
        }
        else if (_client == null)
        {
            throw new InvalidOperationException("UDP connection is not open.");
        }
    }

    /// <summary>Receives the next datagram on the shared socket (from any sender; the token is not observed).</summary>
    /// <param name="cancellationToken">Not observed.</param>
    /// <exception cref="InvalidOperationException">No socket.</exception>
    public override async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (IsConnected && _client != null)
        {
            var result = await _client.ReceiveAsync();
            return result.Buffer;
        }
        else if (_client == null)
        {
            throw new InvalidOperationException("UDP connection is not open.");
        }

        return Array.Empty<byte>();
    }

    /// <summary>No-op.</summary>
    public override Task CloseAsync()
    {
        // UDP is connectionless, so we don’t explicitly "close" connections.
        return Task.CompletedTask;
    }
}



// public sealed class UdpConnectionSetup : TransportConnectionSetup<UdpConnectionSetup>
// {
//     public UdpConnectionSetup(IServiceCollection services, IAltruistContext settings) : base(services, settings)
//     {
//     }
// }

/// <summary>Service token describing the UDP transport (not auto-registered).</summary>
public sealed class UdpTransportToken : ITransportServiceToken
{
    /// <summary>Shared instance.</summary>
    public static UdpTransportToken Instance = new UdpTransportToken();

    /// <summary>Human-readable description shown at startup.</summary>
    public string Description => "📡 Transport: Udp Socket";
}

/// <summary>Transport configuration registered when UDP is enabled; only logs activation.</summary>
[Service(typeof(ITransportConfiguration))]
[ConditionalOnConfig("altruist:server:transport:udp:enabled", "true")]
public sealed class UdpSocketConfiguration : ITransportConfiguration
{
    /// <summary>Set by the framework once configured.</summary>
    public bool IsConfigured { get; set; }

    /// <summary>Logs activation (no services are added).</summary>
    /// <param name="services">Service collection.</param>
    public Task Configure(IServiceCollection services)
    {
        ILoggerFactory factory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger("WebsocketSupport");
        logger.LogInformation("⚡ Tcp Socket support activated. Ready to transmit data across the cosmos in real-time! 🌌");

        return Task.CompletedTask;
    }
}
