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

using System.Buffers.Binary;
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

[Service(typeof(ITransport))]
[ConditionalOnConfig("altruist:server:transport:mode", "tcp")]
public sealed class TcpTransport : ITransport
{
    private readonly int _port;
    private TcpListener? _listener;

    private readonly string _endpoint;

    private readonly ICodec _codec;

    public TcpTransport(
        ICodec codec,
        [AppConfigValue("altruist:server:transport:config:event", "/game")] string @event,
        [AppConfigValue("altruist:server:transport:config:port", "13000")] int port = 5000)
    {
        _port = port;
        _codec = codec;
        _endpoint = @event;
    }

    public void UseTransportEndpoints<TType>(IApplicationBuilder app, string path) where TType : class
    {
        StartTcpServer(app.ApplicationServices.GetRequiredService<IConnectionManager>(), app.ApplicationServices);
    }

    public void UseTransportEndpoints(IApplicationBuilder app, Type type, string path)
    {
        StartTcpServer((app.ApplicationServices.GetRequiredService(type) as IConnectionManager)!, app.ApplicationServices);
    }

    private void StartTcpServer(IConnectionManager connectionManager, IServiceProvider serviceProvider)
    {
        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Start();
        Console.WriteLine($"[TCP] Listening on port {_port}");
        Task.Run(async () =>
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                _ = HandleClient(client, connectionManager, serviceProvider);
            }
        });
    }

    private async Task HandleClient(TcpClient client, IConnectionManager connectionManager, IServiceProvider serviceProvider)
    {
        var networkStream = client.GetStream();
        var clientIp = client.Client.RemoteEndPoint as IPEndPoint;

        var authContext = new SocketAuthContext
        {
            Token = "",
            ClientId = Guid.NewGuid().ToString(),
            ClientIp = clientIp?.Address ?? IPAddress.Loopback,
            ConnectionTimestamp = DateTime.UtcNow
        };

        AuthDetails? authDetails = null;
        var shieldAttribute = connectionManager.GetType().GetCustomAttribute<ShieldAttribute>();

        if (shieldAttribute != null)
        {
            authDetails = await shieldAttribute.AuthenticateNonHttpAsync(serviceProvider, authContext);

            if (authDetails == null)
            {
                var errorMessage = Encoding.UTF8.GetBytes("Authentication failed.");
                await networkStream.WriteAsync(errorMessage, 0, errorMessage.Length);
                client.Close();
                return;
            }
        }

        // Use length-prefixed framing for non-framed codecs (e.g. MessagePack)
        // IFramedCodec (binary structs) handles its own framing via PacketFramer
        var useFraming = _codec is not IFramedCodec;
        var connection = new CachedTcpConnection(new TcpConnection(client, authContext.ClientId, authDetails, lengthPrefixed: useFraming));

        await connectionManager.HandleConnection(connection, _endpoint, authContext.ClientId);
    }

    public void RouteTraffic(IApplicationBuilder app)
    {
        throw new NotImplementedException();
    }
}

public sealed class CachedTcpConnection : AltruistConnection
{
    [JsonIgnore]
    private TcpConnection? _connection;

    public new string Type { get; } = "tcp";

    public CachedTcpConnection(TcpConnection tcpConnection)
    {
        _connection = tcpConnection;
        ConnectionId = tcpConnection.ConnectionId;
        AuthDetails = tcpConnection.AuthDetails;
        LastActivity = tcpConnection.LastActivity;
        RemoteAddress = tcpConnection.RemoteAddress;
        ConnectedAt = tcpConnection.ConnectedAt;
    }

    public CachedTcpConnection(AltruistConnection connection)
    {
        ConnectionId = connection.ConnectionId;
        AuthDetails = connection.AuthDetails;
        LastActivity = connection.LastActivity;
    }

    [JsonIgnore]
    public new bool IsConnected => _connection?.IsConnected ?? false;

    public override async Task SendAsync(byte[] data)
    {
        if (_connection != null)
        {
            await _connection.SendAsync(data);
            LastActivity = DateTime.UtcNow;
        }
    }

    public override async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_connection != null)
        {
            var data = await _connection.ReceiveAsync(cancellationToken);
            if (data.Length > 0) LastActivity = DateTime.UtcNow;
            return data;
        }
        return Array.Empty<byte>();
    }

    public override Task CloseOutputAsync()
    {
        return _connection?.CloseOutputAsync() ?? Task.CompletedTask;
    }

    public override Task CloseAsync()
    {
        return _connection?.CloseAsync() ?? Task.CompletedTask;
    }
}

/// <summary>
/// TCP connection with optional 4-byte length-prefix framing.
/// When lengthPrefixed is true (used for MessagePack and other non-framed codecs),
/// each message is sent/received as: [4-byte LE length][payload bytes].
/// This ensures reliable message boundaries over TCP streams.
/// When lengthPrefixed is false (used for IFramedCodec like binary structs),
/// raw bytes are passed through and the codec's own framer handles boundaries.
/// </summary>
public sealed class TcpConnection : AltruistConnection
{
    public const int DefaultBufferSize = 8192;

    [JsonIgnore]
    private readonly TcpClient _client;

    [JsonIgnore]
    private readonly NetworkStream _networkStream;

    [JsonIgnore]
    private readonly int _bufferSize;

    [JsonIgnore]
    private readonly bool _lengthPrefixed;

    public new string Type { get; } = "tcp";

    [JsonIgnore]
    public new bool IsConnected => _client.Connected;

    public TcpConnection(TcpClient client, string connectionId, AuthDetails? authDetails,
                          int bufferSize = DefaultBufferSize, bool lengthPrefixed = false)
    {
        _client = client;
        _networkStream = client.GetStream();
        _bufferSize = bufferSize;
        _lengthPrefixed = lengthPrefixed;
        ConnectionId = connectionId;
        AuthDetails = authDetails;
        RemoteAddress = client.Client.RemoteEndPoint?.ToString() ?? "";
        ConnectedAt = DateTime.UtcNow;
    }

    public override async Task SendAsync(byte[] data)
    {
        if (!_client.Connected) return;

        if (_lengthPrefixed)
        {
            // Write 4-byte little-endian length prefix, then payload
            var header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, data.Length);
            await _networkStream.WriteAsync(header.AsMemory(), CancellationToken.None);
            await _networkStream.WriteAsync(data.AsMemory(), CancellationToken.None);
        }
        else
        {
            await _networkStream.WriteAsync(data.AsMemory(0, data.Length), CancellationToken.None);
        }
    }

    public override async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_lengthPrefixed)
        {
            // Read 4-byte length prefix
            var header = new byte[4];
            int headerRead = 0;
            while (headerRead < 4)
            {
                int n = await _networkStream.ReadAsync(
                    header.AsMemory(headerRead, 4 - headerRead), cancellationToken);
                if (n == 0) return Array.Empty<byte>();
                headerRead += n;
            }

            int messageLength = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (messageLength <= 0 || messageLength > 16 * 1024 * 1024) // 16MB max
                return Array.Empty<byte>();

            // Read exactly messageLength bytes
            var payload = new byte[messageLength];
            int payloadRead = 0;
            while (payloadRead < messageLength)
            {
                int n = await _networkStream.ReadAsync(
                    payload.AsMemory(payloadRead, messageLength - payloadRead), cancellationToken);
                if (n == 0) return Array.Empty<byte>();
                payloadRead += n;
            }

            return payload;
        }
        else
        {
            // Raw read for framed codecs
            var buffer = new byte[_bufferSize];
            int bytesRead = await _networkStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (bytesRead == 0) return Array.Empty<byte>();
            return buffer.AsSpan(0, bytesRead).ToArray();
        }
    }

    public override Task CloseOutputAsync()
    {
        try { _client.Client.Shutdown(SocketShutdown.Send); } catch { }
        return Task.CompletedTask;
    }

    public override Task CloseAsync()
    {
        try { _networkStream.Close(); } catch { }
        try { _client.Close(); } catch { }
        return Task.CompletedTask;
    }
}

[Service(typeof(ITransportServiceToken))]
[ConditionalOnConfig("altruist:server:transport:mode", "tcp")]
public sealed class TcpTransportToken : ITransportServiceToken
{
    public static TcpTransportToken Instance = new TcpTransportToken();

    public string Description => "📡 Transport: Tcp Socket";
}

[Service(typeof(ITransportConfiguration))]
[ConditionalOnConfig("altruist:server:transport:mode", "tcp")]
public sealed class TcpSocketConfiguration : ITransportConfiguration
{
    public bool IsConfigured { get; set; }

    public Task Configure(IServiceCollection services)
    {
        ILoggerFactory factory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger("WebsocketSupport");
        logger.LogInformation("⚡ Tcp Socket support activated. Ready to transmit data across the cosmos in real-time! 🌌");

        return Task.CompletedTask;
    }
}
