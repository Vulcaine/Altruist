using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;

namespace Altruist.Client;

/// <summary>
/// TCP client speaking the Altruist wire protocol.
///
/// <para><b>Wire shape:</b></para>
/// <list type="bullet">
///   <item>On connect, server sends <c>[4 LE length][UTF-8 ClientId]</c>.</item>
///   <item>Both directions are length-prefix framed: <c>[4 LE length][payload]</c>.</item>
///   <item>Outbound payload is <c>[1 byte gateLen][gate UTF-8][codec-encoded packet]</c>.</item>
///   <item>Inbound payload is the codec-encoded <see cref="MessageEnvelope"/>
///   (fixarray(3) <c>[MC, header, message]</c>).</item>
/// </list>
///
/// <para><b>Two read patterns</b> — pick one per consumer:</para>
/// <list type="bullet">
///   <item><see cref="StartReadLoop"/> spins a background thread that pumps inbound
///   frames into <see cref="IncomingQueue"/>. Drain on your main loop tick. Unity-shape.</item>
///   <item><see cref="DrainAsync"/> awaits inbound frames inside <paramref name="timeout"/>
///   and returns them. Test-shape.</item>
/// </list>
///
/// <para><b>Concurrency:</b> <see cref="SendAsync"/> locks the underlying stream
/// so multi-thread send won't interleave bytes (we caught a real bug in production
/// from this; <c>ConcurrentSendTests</c> is the regression guard).</para>
/// </summary>
[Service]
[ConditionalOnConfig("altruist:client:transport:tcp")]
internal sealed class AltruistTcpClient : IAsyncDisposable, IDisposable
{
    private readonly EndpointConfig _endpoint;
    private readonly IClientCodec _codec;
    private readonly TcpClient _tcp = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private NetworkStream? _stream;

    private Thread? _readThread;
    private volatile bool _readLoopRunning;

    /// <summary>Codec used for both outbound packet encoding and (informationally)
    /// inbound packet decoding. The router does the actual decoding via its own
    /// codec ref — both should be the same instance in production.</summary>
    public IClientCodec Codec => _codec;

    /// <summary>Server-assigned client identity, populated after
    /// <see cref="ConnectAsync"/> completes the handshake.</summary>
    public string ClientId { get; private set; } = "";

    public bool IsConnected => _stream is not null && _tcp.Connected;

    /// <summary>Background-thread inbound queue. Drain from your main loop:
    /// <c>while (client.IncomingQueue.TryDequeue(out var p)) router.Dispatch(p);</c>.
    /// Only populated after <see cref="StartReadLoop"/> is called.</summary>
    public ConcurrentQueue<byte[]> IncomingQueue { get; } = new();

    /// <summary>Fires after <see cref="ConnectAsync"/> has the ClientId.</summary>
    public event Action? OnConnected;

    /// <summary>Fires once when the read loop terminates (peer close, error, or
    /// explicit disconnect). Subscribe before <see cref="StartReadLoop"/>.</summary>
    public event Action? OnDisconnected;

    /// <summary>Fires for caught read-loop errors. Receive loop ends right after.</summary>
    public event Action<Exception>? OnError;

    public AltruistTcpClient(EndpointConfig endpoint, IClientCodec codec)
    {
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
    }

    /// <summary>
    /// DI-friendly ctor. Reads the <c>altruist:client:transport:tcp</c> block
    /// from <see cref="ClientTransportConfig"/> and resolves the codec via
    /// <see cref="ClientCodecResolver"/>. Tests use the
    /// <c>(EndpointConfig, IClientCodec)</c> ctor for explicit wiring.
    /// </summary>
    public AltruistTcpClient(ClientTransportConfig config, ClientCodecResolver codecResolver)
        : this(BuildEndpoint(config), ResolveCodec(config, codecResolver))
    { }

    private static EndpointConfig BuildEndpoint(ClientTransportConfig config)
    {
        var tcp = config?.Tcp ?? throw new InvalidOperationException(
            "altruist:client:transport:tcp section missing — AltruistTcpClient cannot be constructed.");
        return new EndpointConfig(tcp.Host, tcp.Port, tcp.Codec.Provider);
    }

    private static IClientCodec ResolveCodec(ClientTransportConfig config, ClientCodecResolver codecResolver)
    {
        if (codecResolver is null) throw new ArgumentNullException(nameof(codecResolver));
        var tcp = config?.Tcp ?? throw new InvalidOperationException(
            "altruist:client:transport:tcp section missing — AltruistTcpClient cannot be constructed.");
        return codecResolver.Resolve(tcp.Codec.Provider);
    }

    public async Task ConnectAsync(CancellationToken ct = default)
    {
        if (_stream is not null) throw new InvalidOperationException(
            "AltruistTcpClient already connected.");

        await _tcp.ConnectAsync(_endpoint.Host, _endpoint.Port).ConfigureAwait(false);
        _tcp.NoDelay = true;
        _tcp.ReceiveBufferSize = 16384;
        _tcp.SendBufferSize = 16384;
        _stream = _tcp.GetStream();

        var lenBuf = new byte[4];
        await ReadExactAsync(_stream, lenBuf, 4, ct).ConfigureAwait(false);
        var clientIdLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
        if (clientIdLen <= 0 || clientIdLen > 1024)
            throw new InvalidOperationException($"Invalid handshake clientId length: {clientIdLen}");
        var clientIdBuf = new byte[clientIdLen];
        await ReadExactAsync(_stream, clientIdBuf, clientIdLen, ct).ConfigureAwait(false);
        ClientId = Encoding.UTF8.GetString(clientIdBuf);

        OnConnected?.Invoke();
    }

    /// <summary>Frame a packet (gate + codec(payload)) and write it. Thread-safe.</summary>
    public async Task SendAsync<T>(string gate, T payload, CancellationToken ct = default)
    {
        var stream = _stream ?? throw new InvalidOperationException(
            "AltruistTcpClient not connected. Call ConnectAsync first.");

        var gateBytes = Encoding.UTF8.GetBytes(gate);
        if (gateBytes.Length > byte.MaxValue) throw new InvalidOperationException(
            $"Gate name too long ({gateBytes.Length} bytes); max is 255.");

        var payloadBytes = _codec.Serialize(payload);
        var bodyLen = 1 + gateBytes.Length + payloadBytes.Length;
        var frame = new byte[4 + bodyLen];
        BinaryPrimitives.WriteInt32LittleEndian(frame, bodyLen);
        frame[4] = (byte)gateBytes.Length;
        Buffer.BlockCopy(gateBytes, 0, frame, 5, gateBytes.Length);
        Buffer.BlockCopy(payloadBytes, 0, frame, 5 + gateBytes.Length, payloadBytes.Length);

        // SemaphoreSlim — async-friendly mutex. Holds across the await so two
        // concurrent SendAsync calls can't interleave bytes on the wire.
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, 0, frame.Length, ct).ConfigureAwait(false);
            await stream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Start a background thread that reads framed payloads and pushes
    /// them into <see cref="IncomingQueue"/>. Idempotent — calling twice is a no-op.</summary>
    public void StartReadLoop()
    {
        if (_readLoopRunning) return;
        if (_stream is null) throw new InvalidOperationException(
            "AltruistTcpClient not connected. Call ConnectAsync first.");

        _readLoopRunning = true;
        _readThread = new Thread(ReadLoop) { IsBackground = true, Name = "AltruistTcp-Read" };
        _readThread.Start();
    }

    private void ReadLoop()
    {
        var stream = _stream!;
        var lenBuf = new byte[4];
        Exception? terminalError = null;
        try
        {
            while (_readLoopRunning)
            {
                int n;
                try { n = ReadExactSync(stream, lenBuf, 4); }
                catch (IOException) { break; }      // socket aborted by peer / OS
                catch (ObjectDisposedException) { break; }
                if (n == 0) break;

                int payloadLen = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                if (payloadLen <= 0 || payloadLen > 16 * 1024 * 1024)
                {
                    terminalError = new InvalidOperationException(
                        $"Invalid framed payload length: {payloadLen}");
                    break;
                }

                var payload = new byte[payloadLen];
                int got;
                try { got = ReadExactSync(stream, payload, payloadLen); }
                catch (IOException) { break; }
                catch (ObjectDisposedException) { break; }
                if (got == 0) break;

                IncomingQueue.Enqueue(payload);
            }
        }
        catch (Exception ex) when (_readLoopRunning)
        {
            terminalError = ex;
        }
        finally
        {
            _readLoopRunning = false;
            try { if (terminalError is not null) OnError?.Invoke(terminalError); } catch { }
            try { OnDisconnected?.Invoke(); } catch { }
        }
    }

    /// <summary>Async drain — used by tests that want to await batches of frames
    /// up to <paramref name="timeout"/>. Mutually exclusive with
    /// <see cref="StartReadLoop"/> (don't call both on the same instance).</summary>
    public async Task<List<byte[]>> DrainAsync(TimeSpan timeout)
    {
        var packets = new List<byte[]>();
        var stream = _stream;
        if (stream is null) return packets;

        var lenBuf = new byte[4];
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            while (!cts.IsCancellationRequested)
            {
                await ReadExactAsync(stream, lenBuf, 4, cts.Token).ConfigureAwait(false);
                int len = BinaryPrimitives.ReadInt32LittleEndian(lenBuf);
                if (len <= 0 || len > 16 * 1024 * 1024) break;

                var data = new byte[len];
                await ReadExactAsync(stream, data, len, cts.Token).ConfigureAwait(false);
                packets.Add(data);
            }
        }
        catch (OperationCanceledException) { /* timeout */ }
        catch (EndOfStreamException) { /* peer closed */ }
        catch { /* dead socket — return what we have */ }
        return packets;
    }

    private static int ReadExactSync(NetworkStream stream, byte[] buffer, int count)
    {
        int read = 0;
        while (read < count)
        {
            int n = stream.Read(buffer, read, count - read);
            if (n == 0) return 0; // peer closed
            read += n;
        }
        return read;
    }

    private static async Task ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
#if NETSTANDARD2_1
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
#else
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
#endif
            if (n == 0) throw new EndOfStreamException("Stream closed mid-read");
            read += n;
        }
    }

    public void Disconnect()
    {
        _readLoopRunning = false;
        try { _stream?.Close(); } catch { }
        try { _tcp.Close(); } catch { }
        _stream = null;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return default;
    }

    public void Dispose()
    {
        Disconnect();
        try { _tcp.Dispose(); } catch { }
    }
}
