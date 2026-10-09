/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Configuration;

namespace Altruist.Testing;

/// <summary>
/// UDP client pre-configured for the Altruist test server. Same wire shape as
/// <see cref="TestTcpClient"/> — 1-byte event-name length, UTF-8 event name,
/// codec-encoded payload — but no length prefix (UDP datagrams are inherently
/// framed). Each <c>SendAsync</c> writes one datagram.
///
/// <code>
/// public MovementSyncTest(TestUdpClient udp) { _udp = udp; }
///
/// _udp.Bind();                                    // optional — for receiving
/// await _udp.SendAsync("position", new CMove { X = 100, Y = 50 });
/// var packet = await _udp.ReceiveAsync(TimeSpan.FromSeconds(1));
/// </code>
///
/// <para>Endpoint resolution: <c>altruist:server:transport:udp:host</c> /
/// <c>altruist:server:transport:udp:port</c>. Codec resolution:
/// <c>altruist:server:transport:udp:codec:provider</c> with global fallback.</para>
/// </summary>
public sealed class TestUdpClient : IAsyncDisposable, IDisposable
{
    private readonly UdpClient _udp;
    private readonly IPEndPoint _serverEndpoint;

    /// <summary>Codec selected from <c>altruist:server:transport:udp:codec:provider</c> (or the global provider).</summary>
    public ITestCodec Codec { get; }

    /// <summary>A client for <c>altruist:server:transport:udp:host</c> (default <c>localhost</c>, resolved to IPv4 once) and <c>:port</c> (default 13001).</summary>
    /// <param name="cfg">The test configuration.</param>
    public TestUdpClient(IConfiguration cfg)
    {
        var host = TestHttpClient.NormalizeHost(cfg["altruist:server:transport:udp:host"] ?? "localhost");
        var port = int.Parse(cfg["altruist:server:transport:udp:port"] ?? "13001");
        // Resolve hostname to IP so we don't repeat the lookup on every send.
        var hostIp = host == "localhost"
            ? IPAddress.Loopback
            : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
              ?? IPAddress.Loopback;
        _serverEndpoint = new IPEndPoint(hostIp, port);
        _udp = new UdpClient();
        Codec = TestCodecResolver.Resolve(cfg, "udp");
    }

    /// <summary>Bind a local port so the client can receive datagrams. Optional —
    /// fire-and-forget tests don't need it. Pass <c>0</c> to let the OS assign.</summary>
    /// <param name="localPort">Local port, or 0.</param>
    public void Bind(int localPort = 0)
    {
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, localPort));
    }

    /// <summary>Send one datagram with an event name + codec-encoded payload.</summary>
    /// <param name="eventName">The event name (at most 255 UTF-8 bytes).</param>
    /// <param name="payload">The payload, or null for an empty body.</param>
    /// <param name="ct">Cancels waiting for the send.</param>
    /// <exception cref="InvalidOperationException">The event name is longer than 255 bytes.</exception>
    public async Task SendAsync(string eventName, object? payload = null, CancellationToken ct = default)
    {
        var eventBytes = Encoding.UTF8.GetBytes(eventName);
        if (eventBytes.Length > byte.MaxValue)
            throw new InvalidOperationException($"Event name too long ({eventBytes.Length} bytes); max is 255.");

        var payloadBytes = Codec.Serialize(payload);
        var datagram = new byte[1 + eventBytes.Length + payloadBytes.Length];
        datagram[0] = (byte)eventBytes.Length;
        Buffer.BlockCopy(eventBytes, 0, datagram, 1, eventBytes.Length);
        Buffer.BlockCopy(payloadBytes, 0, datagram, 1 + eventBytes.Length, payloadBytes.Length);

        await _udp.SendAsync(datagram, datagram.Length, _serverEndpoint).WaitAsync(ct);
    }

    /// <summary>Receive one datagram or throw <see cref="OperationCanceledException"/>
    /// on timeout. The returned bytes are the datagram body (event-name length + name
    /// + codec payload).</summary>
    /// <param name="timeout">How long to wait.</param>
    public async Task<byte[]> ReceiveAsync(TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var result = await _udp.ReceiveAsync(cts.Token);
        return result.Buffer;
    }

    /// <summary>Same as <see cref="Dispose"/>.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the socket (errors are ignored).</summary>
    public void Dispose()
    {
        try { _udp.Close(); } catch { }
        try { _udp.Dispose(); } catch { }
    }
}
