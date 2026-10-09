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
using System.Collections.Concurrent;
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
/// Tracks TCP connection count, max limit, and login queue.
/// Registered as a singleton service so HTTP controllers can query it.
/// </summary>
public interface IConnectionGate
{
    /// <summary>Currently admitted TCP connections.</summary>
    int ActiveConnections { get; }
    /// <summary>Admission limit (<c>altruist:server:transport:max_connections</c>, default 1000).</summary>
    int MaxConnections { get; }
    /// <summary>Entries in the login queue.</summary>
    int QueueLength { get; }
    /// <summary>True when <see cref="ActiveConnections"/> reached <see cref="MaxConnections"/>.</summary>
    bool IsFull { get; }
    /// <summary>Returns the 1-based queue position of a user, or -1 when not queued.</summary>
    /// <param name="userId">User id (e.g. JWT <c>sub</c>).</param>
    int GetQueuePosition(string userId);
    /// <summary>Rough wait estimate in seconds (position × 6), 0 when not queued.</summary>
    /// <param name="userId">User id.</param>
    int EstimatedWaitSeconds(string userId);
}

/// <summary>
/// Default <see cref="IConnectionGate"/> (singleton). <see cref="TcpTransport"/> calls <see cref="TryAdmit"/> per accepted socket
/// and <see cref="Release"/> when it closes; the queue is maintained by the queue endpoints of <see cref="QueueStatusController"/>
/// and is informational only (admission does not consult it).
/// </summary>
[Service(typeof(IConnectionGate))]
public sealed class ConnectionGate : IConnectionGate
{
    private int _active;
    private readonly int _max;
    private readonly ConcurrentQueue<QueueEntry> _queue = new();
    private readonly ConcurrentDictionary<string, int> _queuePositions = new();

    /// <inheritdoc/>
    public int ActiveConnections => _active;
    /// <inheritdoc/>
    public int MaxConnections => _max;
    /// <inheritdoc/>
    public int QueueLength => _queue.Count;
    /// <inheritdoc/>
    public bool IsFull => _active >= _max;

    /// <summary>Creates the gate.</summary>
    /// <param name="maxConnections"><c>altruist:server:transport:max_connections</c> (default 1000).</param>
    public ConnectionGate(
        [AppConfigValue("altruist:server:transport:max_connections", "1000")] int maxConnections = 1000)
    {
        _max = maxConnections;
    }

    /// <summary>Atomically takes a connection slot; returns false (and takes nothing) when full. Pair every true result with <see cref="Release"/>.</summary>
    public bool TryAdmit()
    {
        var current = Interlocked.Increment(ref _active);
        if (current <= _max) return true;
        Interlocked.Decrement(ref _active);
        return false;
    }

    /// <summary>Frees a slot taken by <see cref="TryAdmit"/>.</summary>
    public void Release() => Interlocked.Decrement(ref _active);

    /// <summary>Appends a user to the login queue (no de-duplication) and recomputes positions.</summary>
    /// <param name="userId">User id.</param>
    public void Enqueue(string userId)
    {
        _queue.Enqueue(new QueueEntry(userId, DateTime.UtcNow));
        RebuildPositions();
    }

    /// <summary>Removes and returns the user at the head of the queue, or <c>null</c> when empty.</summary>
    public string? TryDequeue()
    {
        if (_queue.TryDequeue(out var entry))
        {
            _queuePositions.TryRemove(entry.UserId, out _);
            RebuildPositions();
            return entry.UserId;
        }
        return null;
    }

    /// <summary>
    /// Forgets a user's queue position. Note: the entry itself stays in the underlying queue (it cannot remove from the middle),
    /// so it reappears in positions after the next <see cref="Enqueue"/>/<see cref="TryDequeue"/> and still counts in <see cref="QueueLength"/>.
    /// </summary>
    /// <param name="userId">User id.</param>
    public void RemoveFromQueue(string userId)
    {
        // ConcurrentQueue doesn't support removal, but we track positions
        _queuePositions.TryRemove(userId, out _);
    }

    /// <inheritdoc/>
    public int GetQueuePosition(string userId)
    {
        return _queuePositions.TryGetValue(userId, out var pos) ? pos : -1;
    }

    /// <inheritdoc/>
    public int EstimatedWaitSeconds(string userId)
    {
        var pos = GetQueuePosition(userId);
        return pos <= 0 ? 0 : pos * 6; // ~6 seconds per player based on throughput
    }

    private void RebuildPositions()
    {
        _queuePositions.Clear();
        int i = 1;
        foreach (var entry in _queue)
            _queuePositions[entry.UserId] = i++;
    }

    private record QueueEntry(string UserId, DateTime EnqueuedAt);
}

/// <summary>
/// Raw TCP transport (<c>altruist:server:transport:tcp:enabled: true</c>). Listens on all interfaces at
/// <c>altruist:server:transport:tcp:port</c> (13000) and hands each socket to <see cref="IConnectionManager.HandleConnection"/>
/// with route <c>altruist:server:transport:tcp:event</c> (<c>/game</c>).
/// </summary>
/// <remarks>
/// Wire protocol: on connect the server first sends the generated client id as <c>[int32 LE length][UTF-8]</c>; when the
/// <see cref="IConnectionGate"/> is full it sends <c>SERVER_FULL</c> in the same format and closes. With a codec that is not an
/// <c>IFramedCodec</c> every message is framed as <c>[int32 LE length][payload]</c> (max 16 MB); with an <c>IFramedCodec</c> raw bytes
/// flow and the codec's framer splits them. Authentication uses a <c>ShieldAttribute</c> on the registered connection manager type, if any.
/// </remarks>
[Service(typeof(ITransport))]
[ConditionalOnConfig("altruist:server:transport:tcp:enabled", "true")]
public sealed class TcpTransport : ITransport
{
    private readonly int _port;
    private TcpListener? _listener;

    private readonly string _endpoint;

    private readonly ICodec _codec;
    private ConnectionGate? _gate;

    /// <summary>Always <c>"tcp"</c>.</summary>
    public string TransportType => "tcp";

    /// <summary>DI constructor.</summary>
    /// <param name="codec">Codec; decides whether length-prefix framing is used.</param>
    /// <param name="event">Route assigned to TCP connections (<c>tcp:event</c>, default <c>/game</c>).</param>
    /// <param name="port">Listen port (<c>tcp:port</c>, default 13000).</param>
    public TcpTransport(
        ICodec codec,
        [AppConfigValue("altruist:server:transport:tcp:event", "/game")] string @event,
        [AppConfigValue("altruist:server:transport:tcp:port", "13000")] int port = 13000)
    {
        _port = port;
        _codec = codec;
        _endpoint = @event;
    }

    /// <summary>Starts the listener using the DI <see cref="IConnectionManager"/>; <paramref name="path"/> is ignored.</summary>
    /// <typeparam name="TType">Ignored.</typeparam>
    /// <param name="app">Application (service provider source).</param>
    /// <param name="path">Ignored; the route comes from <c>tcp:event</c>.</param>
    public void UseTransportEndpoints<TType>(IApplicationBuilder app, string path) where TType : class
    {
        StartTcpServer(app.ApplicationServices.GetRequiredService<IConnectionManager>(), app.ApplicationServices);
    }

    /// <summary>Starts the listener using the service of <paramref name="type"/> as the connection manager; <paramref name="path"/> is ignored. Call once.</summary>
    /// <param name="app">Application (service provider source).</param>
    /// <param name="type">Service type resolving to an <see cref="IConnectionManager"/>.</param>
    /// <param name="path">Ignored.</param>
    public void UseTransportEndpoints(IApplicationBuilder app, Type type, string path)
    {
        StartTcpServer((app.ApplicationServices.GetRequiredService(type) as IConnectionManager)!, app.ApplicationServices);
    }

    private void StartTcpServer(IConnectionManager connectionManager, IServiceProvider serviceProvider)
    {
        _gate = serviceProvider.GetService<IConnectionGate>() as ConnectionGate;

        _listener = new TcpListener(IPAddress.Any, _port);
        _listener.Server.SetSocketOption(System.Net.Sockets.SocketOptionLevel.Socket, System.Net.Sockets.SocketOptionName.ReuseAddress, true);
        _listener.Start(backlog: 512);
        var max = _gate?.MaxConnections ?? 1000;
        Console.WriteLine($"[TCP] Listening on port {_port} (max {max} connections)");
        Task.Run(async () =>
        {
            while (true)
            {
                var client = await _listener.AcceptTcpClientAsync();
                client.NoDelay = true;
                client.ReceiveBufferSize = 16384;
                client.SendBufferSize = 16384;

                if (_gate != null && !_gate.TryAdmit())
                {
                    // Server full — send rejection and close
                    try
                    {
                        var msg = Encoding.UTF8.GetBytes("SERVER_FULL");
                        var len = new byte[4];
                        BinaryPrimitives.WriteInt32LittleEndian(len, msg.Length);
                        await client.GetStream().WriteAsync(len);
                        await client.GetStream().WriteAsync(msg);
                    }
                    catch { }
                    client.Close();
                    continue;
                }

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

        // Send clientId to the client as the first message (length-prefixed UTF-8)
        // so the client can use it in the HTTP session upgrade request.
        var clientIdBytes = Encoding.UTF8.GetBytes(authContext.ClientId);
        var lenBytes = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(lenBytes, clientIdBytes.Length);
        await networkStream.WriteAsync(lenBytes);
        await networkStream.WriteAsync(clientIdBytes);

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

        var useFraming = _codec is not IFramedCodec;
        var connection = new CachedTcpConnection(new TcpConnection(client, authContext.ClientId, authDetails, lengthPrefixed: useFraming));
        connection.Route = _endpoint;

        try
        {
            await connectionManager.HandleConnection(connection, _endpoint, authContext.ClientId);
        }
        finally
        {
            _gate?.Release();
        }
    }

    /// <summary>No-op (TCP does not use the HTTP pipeline).</summary>
    /// <param name="app">Unused.</param>
    public void RouteTraffic(IApplicationBuilder app) { }
}

/// <summary>
/// Store-friendly wrapper around a <see cref="TcpConnection"/>: copies its metadata, forwards I/O and updates
/// <c>LastActivity</c> on send/receive. Created by <see cref="TcpTransport"/>; you rarely construct it yourself.
/// </summary>
public sealed class CachedTcpConnection : AltruistConnection
{
    [JsonIgnore]
    private TcpConnection? _connection;

    /// <summary>Transport type tag (hides the base <c>Type</c>; the base property still reports the class name).</summary>
    public new string Type { get; } = "tcp";

    /// <summary>Wraps a live TCP connection.</summary>
    /// <param name="tcpConnection">Underlying connection.</param>
    public CachedTcpConnection(TcpConnection tcpConnection)
    {
        _connection = tcpConnection;
        ConnectionId = tcpConnection.ConnectionId;
        AuthDetails = tcpConnection.AuthDetails;
        LastActivity = tcpConnection.LastActivity;
        RemoteAddress = tcpConnection.RemoteAddress;
        ConnectedAt = tcpConnection.ConnectedAt;
    }

    /// <summary>Creates a detached copy (metadata only): not connected, sends are dropped, receives return empty.</summary>
    /// <param name="connection">Connection to copy id, auth details and last activity from.</param>
    public CachedTcpConnection(AltruistConnection connection)
    {
        ConnectionId = connection.ConnectionId;
        AuthDetails = connection.AuthDetails;
        LastActivity = connection.LastActivity;
    }

    /// <summary>True while the underlying TCP client is connected; the setter is ignored.</summary>
    [JsonIgnore]
    public override bool IsConnected { get => _connection?.IsConnected ?? false; set { } }

    /// <inheritdoc/>
    public override async Task SendAsync(byte[] data)
    {
        if (_connection != null)
        {
            await _connection.SendAsync(data);
            LastActivity = DateTime.UtcNow;
        }
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public override Task CloseOutputAsync()
    {
        return _connection?.CloseOutputAsync() ?? Task.CompletedTask;
    }

    /// <inheritdoc/>
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
    /// <summary>Default raw read buffer size in bytes (non-length-prefixed mode).</summary>
    public const int DefaultBufferSize = 8192;

    [JsonIgnore]
    private readonly TcpClient _client;

    [JsonIgnore]
    private readonly NetworkStream _networkStream;

    [JsonIgnore]
    private readonly int _bufferSize;

    [JsonIgnore]
    private readonly bool _lengthPrefixed;

    /// <summary>Transport type tag (hides the base <c>Type</c>).</summary>
    public new string Type { get; } = "tcp";

    /// <summary>Reflects <see cref="TcpClient.Connected"/>; the setter is ignored.</summary>
    [JsonIgnore]
    public override bool IsConnected { get => _client.Connected; set { } }

    /// <summary>Wraps an accepted <see cref="TcpClient"/>.</summary>
    /// <param name="client">Accepted client.</param>
    /// <param name="connectionId">Connection id.</param>
    /// <param name="authDetails">Authentication result, if any.</param>
    /// <param name="bufferSize">Raw read buffer size in bytes.</param>
    /// <param name="lengthPrefixed">Use <c>[int32 LE length][payload]</c> framing.</param>
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

    /// <summary>Writes one message (length-prefixed when enabled) and flushes; silently does nothing when disconnected.</summary>
    /// <param name="data">Message bytes.</param>
    public override async Task SendAsync(byte[] data)
    {
        if (!_client.Connected) return;

        if (_lengthPrefixed)
        {
            // Combine header + payload into single write to avoid small-packet overhead
            var frame = new byte[4 + data.Length];
            BinaryPrimitives.WriteInt32LittleEndian(frame, data.Length);
            data.CopyTo(frame.AsSpan(4));
            await _networkStream.WriteAsync(frame.AsMemory(), CancellationToken.None);
            await _networkStream.FlushAsync();
        }
        else
        {
            await _networkStream.WriteAsync(data.AsMemory(0, data.Length), CancellationToken.None);
            await _networkStream.FlushAsync();
        }
    }

    /// <summary>
    /// Reads one length-prefixed message, or (raw mode) whatever bytes are available up to the buffer size. Returns an empty
    /// array on close, and also for a length prefix that is non-positive or over 16 MB (which the read loop treats as a close).
    /// </summary>
    /// <param name="cancellationToken">Cancels the read (used for the idle timeout).</param>
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

    /// <summary>Half-closes the socket (shutdown send); the peer sees end of stream.</summary>
    public override Task CloseOutputAsync()
    {
        try { _client.Client.Shutdown(SocketShutdown.Send); } catch { }
        return Task.CompletedTask;
    }

    /// <summary>Closes the stream and the client; errors are ignored.</summary>
    public override Task CloseAsync()
    {
        try { _networkStream.Close(); } catch { }
        try { _client.Close(); } catch { }
        return Task.CompletedTask;
    }
}

/// <summary>Service token announcing the TCP transport (startup banner/registration).</summary>
[Service(typeof(ITransportServiceToken))]
[ConditionalOnConfig("altruist:server:transport:tcp:enabled", "true")]
public sealed class TcpTransportToken : ITransportServiceToken
{
    /// <summary>Shared instance.</summary>
    public static TcpTransportToken Instance = new TcpTransportToken();

    /// <summary>Human-readable description shown at startup.</summary>
    public string Description => "📡 Transport: Tcp Socket";
}

/// <summary>Transport configuration registered when TCP is enabled; only logs activation.</summary>
[Service(typeof(ITransportConfiguration))]
[ConditionalOnConfig("altruist:server:transport:tcp:enabled", "true")]
public sealed class TcpSocketConfiguration : ITransportConfiguration
{
    /// <summary>Set by the framework once configured.</summary>
    public bool IsConfigured { get; set; }

    /// <summary>Logs that TCP support is active (no services are added).</summary>
    /// <param name="services">Service collection.</param>
    public Task Configure(IServiceCollection services)
    {
        ILoggerFactory factory = services.BuildServiceProvider().GetRequiredService<ILoggerFactory>();
        ILogger logger = factory.CreateLogger("WebsocketSupport");
        logger.LogInformation("⚡ Tcp Socket support activated. Ready to transmit data across the cosmos in real-time! 🌌");

        return Task.CompletedTask;
    }
}
