using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;
using Altruist.Authentication;
using Altruist.Contracts;
using Altruist.Transport;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Redis.OM.Modeling;

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
            Token = handshakeMessage.Token,
            ClientId = Guid.NewGuid().ToString(),
            ClientIp = clientIp!.Address,
            ConnectionTimestamp = DateTime.UtcNow
        };

        if (shieldAttribute != null)
        {
            if (clientIp == null || string.IsNullOrEmpty(handshakeMessage.Token))
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

        var connection = new TcpConnection(client, authContext.ClientId, authDetails);

        await connectionManager.HandleConnection(connection, _endpoint, authContext.ClientId);
    }
}

[Document(StorageType = StorageType.Json, IndexName = "Connections", Prefixes = new[] { "tcp" })]
public sealed class TcpConnection : IConnection
{
    [JsonIgnore]
    private readonly TcpClient _client;

    [JsonIgnore]
    private readonly NetworkStream _networkStream;

    public string ConnectionId { get; }

    public bool IsConnected => _client.Connected;

    [JsonIgnore]
    public AuthDetails? AuthDetails { get; }

    public TcpConnection(TcpClient client, string connectionId, AuthDetails? authDetails)
    {
        _client = client;
        _networkStream = client.GetStream();
        ConnectionId = connectionId;
        AuthDetails = authDetails;
    }

    public async Task SendAsync(byte[] data)
    {
        if (IsConnected)
        {
            await _networkStream.WriteAsync(data, 0, data.Length);
        }
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[1024];
        int bytesRead = await _networkStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
        return buffer.Take(bytesRead).ToArray();
    }

    public Task CloseAsync()
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
    public ITransportConfiguration Configuration => new TcpSocketConfiguration();

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