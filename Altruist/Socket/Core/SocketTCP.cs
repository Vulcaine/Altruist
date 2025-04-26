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

public sealed class TcpTransport : ITransport
{
    private readonly int _port;
    private TcpListener? _listener;

    private readonly string _endpoint;

    private readonly ICodec _codec;

    public TcpTransport(ICodec codec, string @event, int port = 5000)
    {
        _port = port;
        _codec = codec;
        _endpoint = @event;
    }

    public void UseTransportEndpoints<TType>(IApplicationBuilder app, string path)
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
        var buffer = new byte[1024];
        var errorMessage = Encoding.UTF8.GetBytes("Authentication failed.");

        var shieldAttribute = connectionManager.GetType().GetCustomAttribute<ShieldAttribute>();
        AuthDetails? authDetails = null;

        var handshakeMessage = _codec.Decoder.Decode<HandshakePacket>(buffer);
        var clientIp = client.Client.RemoteEndPoint as IPEndPoint;

        var authContext = new SocketAuthContext
        {
            Token = "",
            ClientId = Guid.NewGuid().ToString(),
            ClientIp = clientIp!.Address,
            ConnectionTimestamp = DateTime.UtcNow
        };

        if (shieldAttribute != null)
        {
            if (clientIp == null || string.IsNullOrEmpty(""))
            {
                await networkStream.WriteAsync(errorMessage, 0, errorMessage.Length);
                client.Close();
            }

            authDetails = await shieldAttribute.AuthenticateNonHttpAsync(serviceProvider, authContext);

            if (authDetails == null)
            {
                await networkStream.WriteAsync(errorMessage, 0, errorMessage.Length);
                client.Close();
                return;
            }
        }

        var connection = new CachedTcpConnection(new TcpConnection(client, authContext.ClientId, authDetails));

        await connectionManager.HandleConnection(connection, _endpoint, authContext.ClientId);
    }

    public void RouteTraffic(IApplicationBuilder app)
    {
        throw new NotImplementedException();
    }
}

public sealed class CachedTcpConnection : Connection
{
    [JsonIgnore]
    private TcpConnection? _connection;

    public new string Type { get; } = "tcp";

    public CachedTcpConnection(TcpConnection udpConnection)
    {
        _connection = udpConnection;
        ConnectionId = udpConnection.ConnectionId;
        AuthDetails = udpConnection.AuthDetails;
        LastActivity = udpConnection.LastActivity;
    }

    public CachedTcpConnection(Connection connection)
    {
        ConnectionId = connection.ConnectionId;
        AuthDetails = connection.AuthDetails;
        LastActivity = connection.LastActivity;
    }

    [JsonIgnore]
    public new bool IsConnected => DateTime.UtcNow - LastActivity < TimeSpan.FromMinutes(30);

    public override async Task SendAsync(byte[] data)
    {
        if (_connection != null)
        {
            await _connection.SendAsync(data);
        }
    }

    public override async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        if (_connection != null)
        {
            return await _connection.ReceiveAsync(cancellationToken);
        }
        else
        {
            return Array.Empty<byte>();
        }
    }

    public override Task CloseAsync()
    {
        if (_connection != null)
        {
            return _connection.CloseAsync();
        }
        else
        {
            return Task.CompletedTask;
        }
    }
}

public sealed class TcpConnection : Connection
{
    [JsonIgnore]
    private readonly TcpClient _client;

    [JsonIgnore]
    private readonly NetworkStream _networkStream;

    public new string Type { get; } = "tcp";

    public TcpConnection(TcpClient client, string connectionId, AuthDetails? authDetails)
    {
        _client = client;
        _networkStream = client.GetStream();
        ConnectionId = connectionId;
        AuthDetails = authDetails;
    }

    public override async Task SendAsync(byte[] data)
    {
        if (IsConnected)
        {
            await _networkStream.WriteAsync(data, 0, data.Length);
        }
    }

    public override async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        int bytesRead = await _networkStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
        return buffer.Take(bytesRead).ToArray();
    }

    public override Task CloseAsync()
    {
        _networkStream.Close();
        _client.Close();
        return Task.CompletedTask;
    }
}


public sealed class TcpConnectionSetup : TransportConnectionSetup<TcpConnectionSetup>
{
    public TcpConnectionSetup(IServiceCollection services, IAltruistContext settings) : base(services, settings)
    {
    }
}

public sealed class TcpTransportToken : ITransportServiceToken
{
    public static TcpTransportToken Instance = new TcpTransportToken();
    public ITransportConfiguration Configuration { get; } = new TcpSocketConfiguration();

    public string Description => "📡 Transport: Tcp Socket";
}

public sealed class TcpSocketConfiguration : ITransportConfiguration
{
    public void Configure(IServiceCollection services)
    {
        ILoggerFactory factory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger("WebsocketSupport");
        logger.LogInformation("⚡ Tcp Socket support activated. Ready to transmit data across the cosmos in real-time! 🌌");
        services.AddSingleton<ITransport, TcpTransport>();

        services.AddSingleton<TcpConnectionSetup>();
        services.AddSingleton<ITransportConnectionSetupBase>(sp => sp.GetRequiredService<TcpConnectionSetup>());

        services.AddSingleton<ITransportConfiguration, TcpSocketConfiguration>();
        services.AddSingleton<ITransportServiceToken, TcpTransportToken>();
    }
}