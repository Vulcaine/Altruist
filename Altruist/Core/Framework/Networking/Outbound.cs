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

using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist;

/// <summary>
/// <c>altruist:server:transport:outbound:mode</c>: how <see cref="ClientSender.SendAsync{TPacketBase}(string, TPacketBase)"/>
/// (and the room and broadcast senders built on it) deliver packets.
/// </summary>
public enum OutboundMode
{
    /// <summary>Encode and await the socket send on the caller (the behaviour before 0.9.9; default).</summary>
    Direct,

    /// <summary>Hand the packet to the client's outbound queue and return; a pump sends it off the caller.</summary>
    Queued,
}

/// <summary>
/// Marks a packet type whose queued copies supersede each other: a client's outbound queue keeps at
/// most one unsent packet per key (the newest, moved behind everything queued before it), so a
/// client that reads slowly skips stale states instead of building a backlog. Typical use: world
/// snapshots. Applies to <see cref="ClientSender.Enqueue"/> and to queued-mode sends.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true, AllowMultiple = false)]
public sealed class CoalesceAttribute : Attribute
{
    /// <summary>Marks the packet type as coalescing under <paramref name="key"/>.</summary>
    /// <param name="key">Coalesce key; packets sharing it replace each other in a client's queue. Must be non-empty.</param>
    /// <exception cref="ArgumentException"><paramref name="key"/> is null or whitespace.</exception>
    /// <example><code>
    /// [Coalesce("world-snapshot")]
    /// public sealed class WorldSnapshotPacket : IPacketBase { ... }
    /// </code></example>
    public CoalesceAttribute(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            throw new ArgumentException("A coalesce key is required.", nameof(key));
        Key = key;
    }

    /// <summary>Coalesce key shared by packets that supersede each other.</summary>
    public string Key { get; }
}

/// <summary>
/// Counters of the outbound queues. Register an implementation in DI
/// (<c>[Service(typeof(IOutboundMetrics))]</c>) to receive them; none is registered by default.
/// Called from the sending threads and the pumps: implementations must be thread-safe and cheap.
/// </summary>
public interface IOutboundMetrics
{
    /// <summary>Packets waiting in all clients' queues changed by <paramref name="delta"/>.</summary>
    void QueueDepthChanged(int delta);

    /// <summary>Length of one client's queue right after a packet was added to it.</summary>
    void ClientQueueLength(int length);

    /// <summary>A queued packet was superseded by a newer one with the same coalesce key.</summary>
    void Coalesced();

    /// <summary>A client that stopped reading was aborted (send stuck or queue over the limit).</summary>
    void SlowClientClosed();

    /// <summary>A packet of <paramref name="bytes"/> was written to a socket.</summary>
    void Sent(int bytes);

    /// <summary>A queued send failed (the packet is dropped; the queue goes on).</summary>
    void SendError();
}

/// <summary>
/// Per-client outbound queues behind <see cref="ClientSender.Enqueue"/>,
/// <see cref="ClientSender.CloseAfterFlush"/> and queued-mode sends. The caller only enqueues (it
/// never encodes or waits on a socket); one pump per client drains that client's queue on the thread
/// pool, in order, so a slow or stalled client cannot delay the caller or any other client.
/// <list type="bullet">
/// <item>Packets marked <see cref="CoalesceAttribute"/> supersede queued packets with the same key.</item>
/// <item>A client whose send has been in flight longer than <c>stuck-send-seconds</c>, or that has more than
/// <c>max-queued-per-client</c> packets waiting, is not reading: its connection is aborted and the normal
/// disconnect path runs. Both are checked when a packet is enqueued.</item>
/// <item>Packets are encoded like <see cref="ClientSender"/> does (a <see cref="MessageEnvelope"/> stamped
/// for the receiver), into a reused per-client buffer when the codec supports <see cref="IBufferEncoder"/>.</item>
/// </list>
/// One instance per process (every <see cref="ClientSender"/> resolved from DI shares it).
/// Config: <c>altruist:server:transport:outbound:{mode,max-queued-per-client,stuck-send-seconds,encode-buffer-bytes}</c>.
/// </summary>
[Service]
[ConditionalOnConfig("altruist:server:transport")]
public sealed class OutboundQueues
{
    /// <summary>Default for <c>max-queued-per-client</c> (packets).</summary>
    public const int DefaultMaxQueuedPerClient = 256;
    /// <summary>Default for <c>stuck-send-seconds</c> (seconds).</summary>
    public const double DefaultStuckSendSeconds = 5;
    /// <summary>Default for <c>encode-buffer-bytes</c> (bytes).</summary>
    public const int DefaultEncodeBufferBytes = 2048;

    private static readonly ConcurrentDictionary<Type, string?> CoalesceKeys = new();

    private readonly Dictionary<string, ClientQueue> _clients = new();
    private readonly object _clientsLock = new();
    private readonly IConnectionStore _store;
    private readonly ICodec _codec;
    private readonly ILogger _logger;
    private readonly IOutboundMetrics? _metrics;
    private readonly IDashboardNetworkRecorder? _networkRecorder;
    private readonly long _stuckSendTicks;

    /// <summary>DI constructor; values come from <c>altruist:server:transport:outbound:*</c>. Non-positive numeric values fall back to the defaults.</summary>
    /// <param name="store">Connection store used to resolve and abort connections.</param>
    /// <param name="codec">Codec used to encode queued packets.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="metrics">Optional queue metrics sink.</param>
    /// <param name="networkRecorder">Optional dashboard packet recorder.</param>
    /// <param name="mode"><c>mode</c>: <c>direct</c> (default) or <c>queued</c>.</param>
    /// <param name="maxQueuedPerClient"><c>max-queued-per-client</c>.</param>
    /// <param name="stuckSendSeconds"><c>stuck-send-seconds</c>.</param>
    /// <param name="encodeBufferBytes"><c>encode-buffer-bytes</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is not <c>direct</c> or <c>queued</c>.</exception>
    public OutboundQueues(
        IConnectionStore store,
        ICodec codec,
        ILoggerFactory? loggerFactory = null,
        IOutboundMetrics? metrics = null,
        IDashboardNetworkRecorder? networkRecorder = null,
        [AppConfigValue("altruist:server:transport:outbound:mode", "direct")] string? mode = "direct",
        [AppConfigValue("altruist:server:transport:outbound:max-queued-per-client", "256")] int maxQueuedPerClient = DefaultMaxQueuedPerClient,
        [AppConfigValue("altruist:server:transport:outbound:stuck-send-seconds", "5")] double stuckSendSeconds = DefaultStuckSendSeconds,
        [AppConfigValue("altruist:server:transport:outbound:encode-buffer-bytes", "2048")] int encodeBufferBytes = DefaultEncodeBufferBytes)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _codec = codec ?? throw new ArgumentNullException(nameof(codec));
        _logger = loggerFactory?.CreateLogger<OutboundQueues>() ?? (ILogger)NullLogger.Instance;
        _metrics = metrics;
        _networkRecorder = networkRecorder;
        Mode = ParseMode(mode);
        MaxQueuedPerClient = maxQueuedPerClient > 0 ? maxQueuedPerClient : DefaultMaxQueuedPerClient;
        StuckSendLimit = TimeSpan.FromSeconds(stuckSendSeconds > 0 ? stuckSendSeconds : DefaultStuckSendSeconds);
        _stuckSendTicks = (long)(StuckSendLimit.TotalSeconds * Stopwatch.Frequency);
        EncodeBufferBytes = encodeBufferBytes > 0 ? encodeBufferBytes : DefaultEncodeBufferBytes;
    }

    /// <summary><c>outbound:mode</c>: whether <see cref="ClientSender.SendAsync{TPacketBase}(string, TPacketBase)"/> queues.</summary>
    public OutboundMode Mode { get; }

    /// <summary>More packets than this waiting for one client aborts its connection.</summary>
    public int MaxQueuedPerClient { get; }

    /// <summary>A send in flight longer than this (seen on the next enqueue) aborts the connection.</summary>
    public TimeSpan StuckSendLimit { get; }

    /// <summary>Initial size of each client's reused encode buffer (it grows as needed).</summary>
    public int EncodeBufferBytes { get; }

    /// <summary>Parses an <c>outbound:mode</c> value (case-insensitive; empty or null means <see cref="OutboundMode.Direct"/>).</summary>
    /// <param name="value">Configured value.</param>
    /// <returns>The parsed mode.</returns>
    /// <exception cref="ArgumentException">Value is neither <c>direct</c> nor <c>queued</c>.</exception>
    public static OutboundMode ParseMode(string? value)
    {
        var v = (value ?? "").Trim();
        if (v.Length == 0 || v.Equals("direct", StringComparison.OrdinalIgnoreCase))
            return OutboundMode.Direct;
        if (v.Equals("queued", StringComparison.OrdinalIgnoreCase))
            return OutboundMode.Queued;
        throw new ArgumentException($"Unknown altruist:server:transport:outbound:mode '{value}'. Use direct or queued.", nameof(value));
    }

    /// <summary>The <see cref="CoalesceAttribute"/> key of a packet type (null when it has none).</summary>
    public static string? CoalesceKeyOf(Type packetType) =>
        CoalesceKeys.GetOrAdd(packetType, static t => t.GetCustomAttribute<CoalesceAttribute>(inherit: true)?.Key);

    /// <summary>Queues a packet for a client; returns at once.</summary>
    public void Enqueue(string clientId, IPacketBase packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        Add(clientId, new Item(packet, null, CoalesceKeyOf(packet.GetType()), false));
    }

    /// <summary>Queues already encoded bytes for a client; returns at once.</summary>
    public void Enqueue(string clientId, byte[] message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Add(clientId, new Item(null, message, null, false));
    }

    /// <summary>
    /// Closes the client's connection (its output side) once everything queued before has been sent.
    /// The read loop then sees the close (or the idle timeout) and runs the normal disconnect path once.
    /// </summary>
    public void CloseAfterFlush(string clientId) => Add(clientId, new Item(null, null, null, true));

    /// <summary>Drops a client's queue and whatever is still in it (the connection is gone).</summary>
    public void Forget(string clientId)
    {
        ClientQueue? q;
        lock (_clientsLock)
        {
            if (!_clients.Remove(clientId, out q))
                return;
        }
        lock (q)
        {
            q.Dead = true;
            _metrics?.QueueDepthChanged(-q.Items.Count);
            q.Items.Clear();
        }
    }

    /// <summary>Number of clients that have a queue (diagnostics, tests).</summary>
    public int ClientCount
    {
        get
        {
            lock (_clientsLock)
                return _clients.Count;
        }
    }

    private void Add(string clientId, Item item)
    {
        if (string.IsNullOrEmpty(clientId))
            return;
        ClientQueue? q;
        lock (_clientsLock)
        {
            if (!_clients.TryGetValue(clientId, out q))
                _clients[clientId] = q = new ClientQueue(this, clientId, EncodeBufferBytes);
        }
        var start = false;
        var abort = false;
        lock (q)
        {
            if (q.Dead || q.Aborted)
                return;
            if (item.CoalesceKey is { } key)
            {
                var old = q.Items.FindIndex(i => string.Equals(i.CoalesceKey, key, StringComparison.Ordinal));
                if (old >= 0)
                {
                    q.Items.RemoveAt(old);
                    _metrics?.QueueDepthChanged(-1);
                    _metrics?.Coalesced();
                }
            }
            q.Items.Add(item);
            _metrics?.QueueDepthChanged(1);
            _metrics?.ClientQueueLength(q.Items.Count);
            var stuck = q.InFlightSince != 0 && Stopwatch.GetTimestamp() - q.InFlightSince > _stuckSendTicks;
            if (stuck || q.Items.Count > MaxQueuedPerClient)
            {
                abort = q.Aborted = true;
                _metrics?.QueueDepthChanged(-q.Items.Count);
                q.Items.Clear();
            }
            else if (!q.Running)
                start = q.Running = true;
        }
        if (abort)
        {
            _metrics?.SlowClientClosed();
            _logger.LogWarning("aborting connection {Client}: not reading (send stuck or {Max}+ packets queued)", clientId, MaxQueuedPerClient);
            _ = AbortAsync(clientId);
        }
        else if (start)
            ThreadPool.UnsafeQueueUserWorkItem(q, preferLocal: false);
    }

    private async Task PumpAsync(ClientQueue q)
    {
        while (true)
        {
            Item item;
            lock (q)
            {
                if (q.Items.Count == 0 || q.Dead || q.Aborted)
                {
                    q.Running = false;
                    q.InFlightSince = 0;
                    return;
                }
                item = q.Items[0];
                q.Items.RemoveAt(0);
                _metrics?.QueueDepthChanged(-1);
                q.InFlightSince = Stopwatch.GetTimestamp();
            }
            try
            {
                var connection = q.Connection is { IsConnected: true } c ? c : q.Connection = await _store.GetConnectionAsync(q.ClientId);
                if (connection is null || !connection.IsConnected)
                    continue;
                if (item.Close)
                {
                    // Close output only: the read loop sees the close reply (or the idle timeout)
                    // and runs the normal disconnect path exactly once.
                    await connection.CloseOutputAsync();
                    continue;
                }
                await SendAsync(q, connection, item);
            }
            catch (Exception ex)
            {
                _metrics?.SendError();
                _logger.LogDebug("send to {Client} failed: {Message}", q.ClientId, ex.Message);
            }
        }
    }

    private async Task SendAsync(ClientQueue q, AltruistConnection connection, Item item)
    {
        var record = _networkRecorder is { CapturePackets: true };
        var started = record ? Stopwatch.GetTimestamp() : 0;
        int length;
        byte[]? raw = item.Raw;
        if (raw is not null)
        {
            length = raw.Length;
            await connection.SendAsync(raw);
        }
        else
        {
            var envelope = new MessageEnvelope(item.Packet!, q.ClientId);
            envelope.Stamp("server", q.ClientId, DateTime.UtcNow);
            if (_codec.Encoder is IBufferEncoder buffered)
            {
                // Encoded into the client's reused buffer: the pump sends one packet at a time.
                var buffer = q.Buffer;
                buffer.ResetWrittenCount();
                buffered.Encode(buffer, envelope);
                length = buffer.WrittenCount;
                if (record)
                    raw = buffer.WrittenSpan.ToArray();
                await connection.SendAsync(buffer.WrittenMemory);
            }
            else
            {
                raw = _codec.Encoder.Encode(envelope);
                length = raw.Length;
                await connection.SendAsync(raw);
            }
        }
        _metrics?.Sent(length);
        if (record)
        {
            await OutboundRecording.RecordAsync(_networkRecorder!, _store, q.ClientId, connection, item.Packet, raw ?? Array.Empty<byte>(),
                item.Packet?.GetType().Name, item.Packet?.MessageCode.ToString(),
                Stopwatch.GetElapsedTime(started).TotalMilliseconds, null);
        }
    }

    private async Task AbortAsync(string clientId)
    {
        try
        {
            var connection = await _store.GetConnectionAsync(clientId);
            connection?.Abort();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("abort {Client} failed: {Message}", clientId, ex.Message);
        }
    }

    private readonly record struct Item(IPacketBase? Packet, byte[]? Raw, string? CoalesceKey, bool Close);

    /// <summary>One client's queue; queued to the thread pool as its own work item (no per-start allocation).</summary>
    private sealed class ClientQueue : IThreadPoolWorkItem
    {
        private readonly OutboundQueues _owner;
        private readonly int _bufferBytes;
        private ArrayBufferWriter<byte>? _buffer;

        public ClientQueue(OutboundQueues owner, string clientId, int bufferBytes)
        {
            _owner = owner;
            ClientId = clientId;
            _bufferBytes = bufferBytes;
        }

        public readonly string ClientId;
        public readonly List<Item> Items = new(4);
        public ArrayBufferWriter<byte> Buffer => _buffer ??= new ArrayBufferWriter<byte>(_bufferBytes);
        public AltruistConnection? Connection;
        public bool Running;
        public bool Dead;
        public bool Aborted;

        /// <summary>Stopwatch timestamp of the send in progress (0 = none).</summary>
        public long InFlightSince;

        public void Execute() => _ = _owner.PumpAsync(this);
    }
}

/// <summary>Dashboard capture of outbound packets, shared by the direct and the queued send paths.</summary>
internal static class OutboundRecording
{
    public static async Task RecordAsync(
        IDashboardNetworkRecorder recorder,
        IConnectionStore store,
        string clientId,
        AltruistConnection socket,
        object? payload,
        byte[] rawPayload,
        string? packetType,
        string? gate,
        double sendDurationMs,
        string? error,
        double? encodeDurationMs = null)
    {
        if (!recorder.CapturePackets)
            return;

        string? roomId = null;
        try
        {
            roomId = (await store.FindRoomForClientAsync(clientId))?.Id;
        }
        catch
        {
            roomId = null;
        }

        await recorder.RecordAsync(new DashboardNetworkEvent
        {
            Kind = "packet",
            Direction = "outbound",
            Transport = socket.GetType().Name,
            Route = socket.Route,
            Gate = gate,
            PacketType = packetType,
            ConnectionId = socket.ConnectionId,
            ClientId = clientId,
            RoomId = roomId,
            DurationMs = sendDurationMs + (encodeDurationMs ?? 0),
            EncodeDurationMs = encodeDurationMs,
            SendDurationMs = sendDurationMs,
            Error = error
        }, payload, rawPayload);
    }
}
