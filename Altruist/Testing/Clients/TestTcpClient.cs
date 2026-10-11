/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

using Microsoft.Extensions.Configuration;

namespace Altruist.Testing;

/// <summary>
/// TCP client pre-configured to talk to the Altruist test server. Knows the
/// Altruist wire framing — 4-byte little-endian length prefix, 1-byte event-name
/// length, UTF-8 event name, codec-encoded payload — so tests don't reimplement
/// it per file.
///
/// <code>
/// public PartyTest(TestTcpClient tcp1, TestTcpClient tcp2) { _t1 = tcp1; _t2 = tcp2; }
///
/// await _t1.ConnectAsync();              // reads server-assigned ClientId
/// await _t1.SendAsync("enter-world", new CEnterGame());
/// var packets = await _t1.DrainAsync(TimeSpan.FromSeconds(1));
/// </code>
///
/// <para>Each ctor param of type <see cref="TestTcpClient"/> gets its own
/// instance — multi-client scenarios (party invites, exchange, PvP) just take
/// two parameters.</para>
///
/// <para>Endpoint resolution: <c>altruist:server:transport:tcp:host</c> /
/// <c>altruist:server:transport:tcp:port</c>. Codec resolution:
/// <c>altruist:server:transport:tcp:codec:provider</c> with fallback to
/// <c>altruist:server:transport:codec:provider</c>. Wildcard binds rewrite to
/// localhost.</para>
/// </summary>
public sealed class TestTcpClient : IAsyncDisposable, IDisposable
{
    private readonly string _host;
    private readonly int _port;
    private readonly TcpClient _tcp = new();
    private NetworkStream? _stream;

    /// <summary>Codec selected from <c>config.yml</c>'s
    /// <c>transport:tcp:codec:provider</c> (or global <c>transport:codec:provider</c>).
    /// Exposed so tests can decode raw inbound bytes themselves if needed.</summary>
    public ITestCodec Codec { get; }

    /// <summary>Server-assigned client identity, read off the first framed
    /// message after connect. Empty until <see cref="ConnectAsync"/> completes.</summary>
    public string ClientId { get; private set; } = "";

    /// <summary>Connected and not disposed.</summary>
    public bool IsConnected => _stream is not null && _tcp.Connected;

    /// <summary>An unconnected client for <c>altruist:server:transport:tcp:host</c> (default <c>localhost</c>) and <c>:port</c> (default 13000).</summary>
    /// <param name="cfg">The test configuration.</param>
    public TestTcpClient(IConfiguration cfg)
    {
        _host = TestHttpClient.NormalizeHost(cfg["altruist:server:transport:tcp:host"] ?? "localhost");
        _port = int.Parse(cfg["altruist:server:transport:tcp:port"] ?? "13000");
        Codec = TestCodecResolver.Resolve(cfg, "tcp");
    }

    /// <summary>Open the TCP connection and consume the server's initial framed
    /// <c>ClientId</c> message. Not idempotent: a second call on the same instance throws.</summary>
    /// <param name="ct">Cancels the connect and the id read.</param>
    /// <exception cref="InvalidOperationException">Already connected.</exception>
    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_stream is not null)
            throw new InvalidOperationException("TestTcpClient already connected.");

        await _tcp.ConnectAsync(_host, _port, ct);
        _tcp.NoDelay = true;
        _tcp.ReceiveBufferSize = 16384;
        _tcp.SendBufferSize = 16384;
        _stream = _tcp.GetStream();

        var lenBuf = new byte[4];
        await _stream.ReadExactlyAsync(lenBuf, ct);
        var clientIdLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        var clientIdBuf = new byte[clientIdLen];
        await _stream.ReadExactlyAsync(clientIdBuf, ct);
        ClientId = Encoding.UTF8.GetString(clientIdBuf);
    }

    /// <summary>Frame an Altruist packet (event name + codec-encoded payload) and
    /// write it. Throws if not connected — call <see cref="ConnectAsync"/> first.</summary>
    /// <param name="eventName">The event (gate) name the server routes on (at most 255 UTF-8 bytes).</param>
    /// <param name="payload">The payload, encoded with <see cref="Codec"/>; null sends an empty body.</param>
    /// <param name="ct">Cancels the write.</param>
    /// <exception cref="InvalidOperationException">Not connected.</exception>
    public async Task SendAsync(string eventName, object? payload = null, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException(
            "TestTcpClient not connected. Call ConnectAsync first.");

        var eventBytes = Encoding.UTF8.GetBytes(eventName);
        var payloadBytes = Codec.Serialize(payload);

        var frameLen = 1 + eventBytes.Length + payloadBytes.Length;
        var frame = new byte[4 + frameLen];
        BinaryPrimitives.WriteInt32LittleEndian(frame, frameLen);
        frame[4] = (byte)eventBytes.Length;
        Buffer.BlockCopy(eventBytes, 0, frame, 5, eventBytes.Length);
        Buffer.BlockCopy(payloadBytes, 0, frame, 5 + eventBytes.Length, payloadBytes.Length);

        await stream.WriteAsync(frame, ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>Read all frames the server sends within <paramref name="timeout"/>.
    /// Returns the raw bodies (codec-encoded — caller decodes with
    /// <see cref="Codec"/> or <see cref="TryParseSyncPacket"/>). Not connected: an empty list.
    /// A frame longer than 64 KiB stops the drain, and a closed or failed socket ends it early;
    /// a frame cut off by the timeout is lost.</summary>
    /// <param name="timeout">How long to keep reading.</param>
    public async Task<List<byte[]>> DrainAsync(TimeSpan timeout)
    {
        var packets = new List<byte[]>();
        var stream = _stream;
        if (stream is null) return packets;

        var lenBuf = new byte[4];
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!cts.Token.IsCancellationRequested)
            {
                int headerRead = 0;
                while (headerRead < 4)
                {
                    int n = await stream.ReadAsync(lenBuf.AsMemory(headerRead, 4 - headerRead), cts.Token);
                    if (n == 0) return packets;
                    headerRead += n;
                }

                int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                if (len <= 0 || len > 65536) break;

                var data = new byte[len];
                int payloadRead = 0;
                while (payloadRead < len)
                {
                    int n = await stream.ReadAsync(data.AsMemory(payloadRead, len - payloadRead), cts.Token);
                    if (n == 0) return packets;
                    payloadRead += n;
                }
                packets.Add(data);
            }
        }
        catch (OperationCanceledException) { /* timeout — return what we have */ }
        catch { /* dead socket — return what we have */ }
        return packets;
    }

    /// <summary>Try to deserialize a raw frame body as a <c>SyncPacket</c>
    /// (Altruist message-code 3). Returns the inner data dictionary or
    /// <c>null</c> if the packet is something else.</summary>
    /// <param name="data">A frame body from <see cref="DrainAsync"/>.</param>
    public Dictionary<string, object?>? TryParseSyncPacket(byte[] data)
    {
        var arr = Codec.DeserializeArray(data);
        if (arr is null || arr.Length < 3) return null;
        try
        {
            var code = Convert.ToUInt32(arr[0]);
            if (code != 3) return null;
            if (arr[2] is object[] syncArr && syncArr.Length >= 3 &&
                syncArr[2] is Dictionary<object, object?> dict)
            {
                return dict.ToDictionary(k => k.Key?.ToString() ?? "", k => k.Value);
            }
            return null;
        }
        catch { return null; }
    }

    /// <summary>Same as <see cref="Dispose"/>.</summary>
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Closes the connection (errors are ignored); the client cannot reconnect.</summary>
    public void Dispose()
    {
        try { _stream?.Close(); } catch { }
        try { _tcp.Close(); } catch { }
        _stream = null;
    }
}
