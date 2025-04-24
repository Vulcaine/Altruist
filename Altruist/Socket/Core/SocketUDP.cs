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
using Altruist.Security;
using Altruist.Contracts;
using Altruist.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Socket;

public sealed class UdpTransport : ITransport
{
    private readonly int _port;
    private UdpClient? _udpClient;
    private readonly string _endpoint;
    private readonly ICodec _codec;

    private readonly IConnectionStore _store;

    private static readonly IMurmurHash3 _hasher = MurmurHash3Factory.Instance.Create();

    public UdpTransport(IConnectionStore store, ICodec codec, string @event, int port = 5000)
    {
        _port = port;
        _codec = codec;
        _endpoint = @event;
        _store = store;
    }

    public void UseTransportEndpoints<TType>(IApplicationBuilder app, string path)
    {
        StartUdpServer(app.ApplicationServices.GetRequiredService<IConnectionManager>(), app.ApplicationServices);
    }

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

            var handshakeMessage = _codec.Decoder.Decode<HandshakePacket>(buffer);

            var authContext = new SocketAuthContext
            {
                Token = handshakeMessage.Token,
                ClientId = clientId,
                ClientIp = clientIp.Address,
                ConnectionTimestamp = DateTime.UtcNow
            };

            if (shieldAttribute != null)
            {
                if (string.IsNullOrEmpty(handshakeMessage.Token))
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

    public void RouteTraffic(IApplicationBuilder app)
    {
        throw new NotImplementedException();
    }
}

public sealed class CachedUdpConnection : Connection
{
    [JsonIgnore]
    private UdpConnection? _udpConnection;

    public new string Type { get; } = "udp";

    public CachedUdpConnection(UdpConnection udpConnection)
    {
        _udpConnection = udpConnection;
        ConnectionId = udpConnection.ConnectionId;
        AuthDetails = udpConnection.AuthDetails;
        LastActivity = udpConnection.LastActivity;
    }

    public CachedUdpConnection(Connection connection)
    {
        ConnectionId = connection.ConnectionId;
        AuthDetails = connection.AuthDetails;
        LastActivity = connection.LastActivity;
    }

    [JsonIgnore]
    public new bool IsConnected => DateTime.UtcNow - LastActivity < TimeSpan.FromMinutes(30);

    public override async Task SendAsync(byte[] data)
    {
        if (_udpConnection != null)
        {
            await _udpConnection.SendAsync(data);
        }
    }

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

public sealed class UdpConnection : Connection
{
    [JsonIgnore]
    private readonly UdpClient? _client;

    [JsonIgnore]
    private readonly IPEndPoint? _remoteEndPoint;

    [JsonIgnore]
    public new bool IsConnected => DateTime.UtcNow - LastActivity < TimeSpan.FromMinutes(30);

    public new string Type { get; } = "udp";

    public UdpConnection(UdpClient client, string connectionId, AuthDetails? authDetails, IPEndPoint remoteEndPoint)
    {
        _client = client;
        ConnectionId = connectionId;
        AuthDetails = authDetails;
        _remoteEndPoint = remoteEndPoint;
    }

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

    public override Task CloseAsync()
    {
        // UDP is connectionless, so we don’t explicitly "close" connections.
        return Task.CompletedTask;
    }
}



public sealed class UdpConnectionSetup : TransportConnectionSetup<UdpConnectionSetup>
{
    public UdpConnectionSetup(IServiceCollection services, IAltruistContext settings) : base(services, settings)
    {
    }
}

public sealed class UdpTransportToken : ITransportServiceToken
{
    public static UdpTransportToken Instance = new UdpTransportToken();
    public ITransportConfiguration Configuration => new UdpSocketConfiguration();

    public string Description => "📡 Transport: Udp Socket";
}


public sealed class UdpSocketConfiguration : ITransportConfiguration
{
    public void Configure(IServiceCollection services)
    {
        ILoggerFactory factory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger("WebsocketSupport");
        logger.LogInformation("⚡ Udp Socket support activated. Ready to transmit data across the cosmos in real-time! 🌌");
        services.AddSingleton<ITransport, UdpTransport>();

        services.AddSingleton<UdpConnectionSetup>();
        services.AddSingleton<ITransportConnectionSetupBase>(sp => sp.GetRequiredService<UdpConnectionSetup>());

        services.AddSingleton<ITransportConfiguration, UdpSocketConfiguration>();
        services.AddSingleton<ITransportServiceToken, UdpTransportToken>();
    }
}