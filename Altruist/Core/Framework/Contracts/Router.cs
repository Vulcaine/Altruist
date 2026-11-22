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

using Altruist.Networking;

using System.Diagnostics;

namespace Altruist;

/// <summary>A sender that delivers a packet to a target identified by a string id (a client id or a room id, depending on the implementation).</summary>
public interface IAltruistRouterSender
{
    /// <summary>Encodes and sends <paramref name="message"/> to the target.</summary>
    /// <typeparam name="TPacketBase">The packet type.</typeparam>
    /// <param name="clientId">The target id: a client id for <see cref="ClientSender"/>, a room id for <see cref="RoomSender"/>.</param>
    /// <param name="message">The packet to send.</param>
    Task SendAsync<TPacketBase>(string clientId, TPacketBase message) where TPacketBase : IPacketBase;
}

/// <summary>
/// Sends the changed properties of an <see cref="ISynchronizedEntity"/> to clients (delta sync). Available through
/// <see cref="IAltruistRouter.Synchronize"/>. The working implementation comes with the gaming module (registered
/// when <c>altruist:game</c> is configured); without it the default implementation throws.
/// </summary>
public interface IClientSynchronizator
{
    /// <summary>
    /// Computes which synced properties changed since the last send for the current engine tick and, if any did,
    /// broadcasts a sync packet with just those properties to all connected clients. Sends nothing when nothing changed.
    /// </summary>
    /// <param name="entity">The entity whose state to sync.</param>
    /// <param name="forceAllAsChanged"><c>true</c> to send every synced property (e.g. a full snapshot for a newly joined client).</param>
    Task SendAsync(ISynchronizedEntity entity, bool forceAllAsChanged = false);
}

/// <summary>
/// The main send API: one entry point with a sender per audience. Inject <see cref="IAltruistRouter"/> (singleton)
/// in portals and services that push packets to clients.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="Client"/>: one client by id (<c>Client.SendAsync(clientId, packet)</c>), or queued/ordered with <c>Client.Enqueue</c>.</item>
/// <item><see cref="Room"/>: every connected member of a room (<c>Room.SendAsync(roomId, packet)</c>).</item>
/// <item><see cref="Broadcast"/>: every connected client, optionally excluding one.</item>
/// <item><see cref="Synchronize"/>: delta-sync of an <see cref="ISynchronizedEntity"/> (gaming module).</item>
/// </list>
/// The registered implementation depends on config: with <c>altruist:game:engine</c> an engine-aware router
/// (<c>InMemoryEngineRouter</c>) whose client sender defers sync packets to the engine tick; otherwise a direct router
/// (<c>InMemoryDirectRouter</c> when <c>altruist:persistence:cache:provider</c> is <c>inmemory</c>).
/// </remarks>
/// <example>
/// <code>
/// [Portal("/chat")]
/// public class ChatPortal : Portal
/// {
///     private readonly IAltruistRouter _router;
///     public ChatPortal(IAltruistRouter router) =&gt; _router = router;
///
///     [Gate("say")]
///     public Task Say(ChatPacket packet, string clientId)
///         =&gt; _router.Broadcast.SendAsync(packet, excludeClientId: clientId);
/// }
/// </code>
/// </example>
public interface IAltruistRouter
{
    /// <summary>Sends to a single client by connection id.</summary>
    ClientSender Client { get; }
    /// <summary>Sends to every connected member of a room.</summary>
    RoomSender Room { get; }
    /// <summary>Sends to every connected client.</summary>
    BroadcastSender Broadcast { get; }
    /// <summary>Delta-syncs entity state to clients.</summary>
    IClientSynchronizator Synchronize { get; }
}

/// <summary>
/// Placeholder <see cref="IClientSynchronizator"/> registered when only the transport is configured. Throws
/// <see cref="NotImplementedException"/>; enable the gaming module (<c>altruist:game</c>) for the working implementation.
/// </summary>
[Service(typeof(IClientSynchronizator))]
[ConditionalOnConfig("altruist:server:transport")]
public class ClientSynchronizator : IClientSynchronizator
{
    /// <summary>Always throws <see cref="NotImplementedException"/>.</summary>
    /// <param name="entity">Ignored.</param>
    /// <param name="forceAllAsChanged">Ignored.</param>
    /// <exception cref="NotImplementedException">Always.</exception>
    public Task SendAsync(ISynchronizedEntity entity, bool forceAllAsChanged = false)
    {
        throw new NotImplementedException($"ClientSynchronizator.SendAsync() is not implemented. Only working with a gaming module.");
    }
}

/// <summary>
/// Base <see cref="IAltruistRouter"/> that just exposes the injected senders. Derive from <see cref="DirectRouter"/>
/// or <c>EngineRouter</c> rather than from this directly.
/// </summary>
public abstract class AbstractAltruistRouter : IAltruistRouter
{
    /// <summary>The connection store the senders resolve clients from.</summary>
    protected readonly IConnectionStore _connectionStore;
    /// <summary>The packet codec.</summary>
    protected readonly ICodec _codec;

    /// <inheritdoc/>
    public ClientSender Client { get; }

    /// <inheritdoc/>
    public RoomSender Room { get; }

    /// <inheritdoc/>
    public BroadcastSender Broadcast { get; }

    /// <inheritdoc/>
    public IClientSynchronizator Synchronize { get; }

    /// <summary>Creates the router over the given senders.</summary>
    /// <param name="store">The connection store.</param>
    /// <param name="codec">The packet codec.</param>
    /// <param name="clientSender">Sender for single clients.</param>
    /// <param name="roomSender">Sender for rooms.</param>
    /// <param name="broadcastSender">Sender for all clients.</param>
    /// <param name="clientSynchronizator">Entity delta-sync sender.</param>
    public AbstractAltruistRouter(IConnectionStore store, ICodec codec, ClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, IClientSynchronizator clientSynchronizator)
    {
        _connectionStore = store;
        _codec = codec;

        Client = clientSender;
        Room = roomSender;
        Broadcast = broadcastSender;
        Synchronize = clientSynchronizator;
    }
}

/// <summary>
/// Router whose sends go straight to the senders (no engine deferral). Used when no game engine is configured;
/// see <c>EngineRouter</c> for the engine-aware variant.
/// </summary>
public abstract class DirectRouter : AbstractAltruistRouter
{
    /// <summary>Creates the router over the given senders.</summary>
    /// <param name="store">The connection store.</param>
    /// <param name="codec">The packet codec.</param>
    /// <param name="clientSender">Sender for single clients.</param>
    /// <param name="roomSender">Sender for rooms.</param>
    /// <param name="broadcastSender">Sender for all clients.</param>
    /// <param name="clientSynchronizator">Entity delta-sync sender.</param>
    protected DirectRouter(IConnectionStore store, ICodec codec, ClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, IClientSynchronizator clientSynchronizator) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator)
    {
    }
}

/// <summary>
/// Sends packets to one client. <see cref="SendAsync{TPacketBase}(string, TPacketBase)"/> encodes and awaits the
/// socket (<c>altruist:server:transport:outbound:mode: direct</c>, the default) or hands the packet to the
/// client's outbound queue and returns (<c>queued</c>). <see cref="Enqueue"/> and <see cref="CloseAfterFlush"/>
/// always use the queue (see <see cref="OutboundQueues"/>): in order per client, never waiting on a socket,
/// with <see cref="CoalesceAttribute"/> packets superseding each other and slow readers aborted.
/// </summary>
[Service]
[ConditionalOnConfig("altruist:server:transport")]
public class ClientSender : IAltruistRouterSender
{
    /// <summary>The connection store clients are resolved from.</summary>
    protected readonly IConnectionStore _store;
    /// <summary>The codec used to encode packets.</summary>
    protected readonly ICodec _codec;
    /// <summary>Optional dashboard recorder for outbound packet capture.</summary>
    protected readonly IDashboardNetworkRecorder? _networkRecorder;
    private OutboundQueues? _outbound;

    /// <summary>Creates a sender without shared outbound queues (it creates its own on first queued use). For manual construction and tests.</summary>
    /// <param name="store">The connection store to resolve clients from.</param>
    /// <param name="codec">The codec used to encode packets.</param>
    /// <param name="networkRecorder">Optional dashboard recorder for outbound packet capture.</param>
    public ClientSender(IConnectionStore store, ICodec codec, IDashboardNetworkRecorder? networkRecorder = null)
        : this(store, codec, outbound: null, networkRecorder)
    {
    }

    /// <summary>The constructor DI uses: the process-wide <see cref="OutboundQueues"/> (shared by every sender).</summary>
    public ClientSender(IConnectionStore store, ICodec codec, OutboundQueues? outbound, IDashboardNetworkRecorder? networkRecorder = null)
    {
        _store = store;
        _codec = codec;
        _networkRecorder = networkRecorder;
        _outbound = outbound;
    }

    /// <summary>
    /// The per-client outbound queues. Shared by every sender resolved from DI; a sender constructed
    /// by hand without one gets its own (direct mode, default limits) on first use.
    /// </summary>
    public OutboundQueues Outbound
    {
        get
        {
            if (_outbound is { } q)
                return q;
            Interlocked.CompareExchange(ref _outbound, new OutboundQueues(_store, _codec, networkRecorder: _networkRecorder), null);
            return _outbound!;
        }
    }

    /// <summary><c>altruist:server:transport:outbound:mode</c>.</summary>
    public OutboundMode Mode => _outbound?.Mode ?? OutboundMode.Direct;

    /// <summary>
    /// Queues a packet for the client and returns at once; a pump encodes and sends it off the caller,
    /// after everything queued before it.
    /// </summary>
    public virtual void Enqueue(string clientId, IPacketBase packet) => Outbound.Enqueue(clientId, packet);

    /// <summary>Closes the client's connection once everything queued before has been sent.</summary>
    public virtual void CloseAfterFlush(string clientId) => Outbound.CloseAfterFlush(clientId);

    /// <summary>
    /// Drops the client's queue and whatever is still in it. The connection manager calls this when a
    /// connection is gone; call it again if you queued for the client after its disconnect.
    /// </summary>
    public virtual void Forget(string clientId) => _outbound?.Forget(clientId);

    /// <summary>
    /// Sends already-encoded bytes to one client: awaits the socket write in <c>direct</c> mode, or enqueues and returns
    /// in <c>queued</c> mode. Does nothing when the client is unknown or not connected. Use the generic overload to send packets.
    /// </summary>
    /// <param name="clientId">The connection id.</param>
    /// <param name="message">The encoded message.</param>
    public virtual async Task SendAsync(string clientId, byte[] message)
    {
        if (Mode == OutboundMode.Queued)
        {
            Outbound.Enqueue(clientId, message);
            return;
        }

        var socket = await _store.GetConnectionAsync(clientId);
        if (socket != null && socket.IsConnected)
        {
            var watch = Stopwatch.StartNew();
            string? error = null;
            try
            {
                await socket.SendAsync(message);
            }
            catch (Exception ex)
            {
                error = ex.Message;
                throw;
            }
            finally
            {
                watch.Stop();
                await RecordOutboundAsync(clientId, socket, null, message, null, null, watch.Elapsed.TotalMilliseconds, error);
            }
        }
    }

    /// <summary>
    /// Encodes the packet (wrapped in a <c>MessageEnvelope</c>) and sends it to one client. In <c>direct</c> mode
    /// (default) the task completes after the socket write and socket exceptions propagate; in <c>queued</c> mode it
    /// enqueues and returns at once (same as <see cref="Enqueue"/>). Unknown or disconnected clients are silently skipped.
    /// </summary>
    /// <typeparam name="TPacketBase">The packet type.</typeparam>
    /// <param name="clientId">The connection id.</param>
    /// <param name="message">The packet to send.</param>
    public virtual async Task SendAsync<TPacketBase>(string clientId, TPacketBase message) where TPacketBase : IPacketBase
    {
        if (Mode == OutboundMode.Queued)
        {
            Outbound.Enqueue(clientId, message);
            return;
        }

        var envelope = new MessageEnvelope(message, clientId);
        envelope.Stamp("server", clientId, DateTime.UtcNow);
        var encodeWatch = Stopwatch.StartNew();
        var encodedMessage = _codec.Encoder.Encode(envelope);
        encodeWatch.Stop();
        await SendEncodedPacketAsync(clientId, encodedMessage, message, encodeWatch.Elapsed.TotalMilliseconds);
    }

    /// <summary>Writes an encoded packet to the client's socket and records it; the override point for direct-mode delivery.</summary>
    /// <typeparam name="TPacketBase">The packet type.</typeparam>
    /// <param name="clientId">The connection id.</param>
    /// <param name="encodedMessage">The encoded bytes.</param>
    /// <param name="message">The original packet (for metrics).</param>
    /// <param name="encodeDurationMs">Time spent encoding, in milliseconds.</param>
    protected virtual async Task SendEncodedPacketAsync<TPacketBase>(
        string clientId,
        byte[] encodedMessage,
        TPacketBase message,
        double encodeDurationMs) where TPacketBase : IPacketBase
    {
        var socket = await _store.GetConnectionAsync(clientId);
        if (socket == null || !socket.IsConnected)
            return;

        var watch = Stopwatch.StartNew();
        string? error = null;
        try
        {
            await socket.SendAsync(encodedMessage);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            throw;
        }
        finally
        {
            watch.Stop();
            await RecordOutboundAsync(
                clientId,
                socket,
                message,
                encodedMessage,
                message.GetType().Name,
                message.MessageCode.ToString(),
                watch.Elapsed.TotalMilliseconds,
                error,
                encodeDurationMs);
        }
    }

    /// <summary>Records an outbound send to the dashboard network recorder when packet capture is on; otherwise does nothing.</summary>
    /// <param name="clientId">The connection id.</param>
    /// <param name="socket">The connection written to.</param>
    /// <param name="payload">The packet object, or <c>null</c> for raw bytes.</param>
    /// <param name="rawPayload">The encoded bytes.</param>
    /// <param name="packetType">Packet type name, if known.</param>
    /// <param name="gate">The packet's message code, if known.</param>
    /// <param name="sendDurationMs">Socket write time, in milliseconds.</param>
    /// <param name="error">The send error message, or <c>null</c>.</param>
    /// <param name="encodeDurationMs">Encode time, in milliseconds, if measured.</param>
    protected virtual Task RecordOutboundAsync(
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
        if (_networkRecorder is null || !_networkRecorder.CapturePackets)
            return Task.CompletedTask;

        return OutboundRecording.RecordAsync(_networkRecorder, _store, clientId, socket, payload, rawPayload, packetType, gate,
            sendDurationMs, error, encodeDurationMs);
    }
}

/// <summary>
/// Sends a packet to every connected member of a room, one <see cref="ClientSender"/> send per member, in sequence.
/// Registered as a singleton when <c>altruist:server:transport</c> is configured; use it through <see cref="IAltruistRouter.Room"/>.
/// </summary>
[Service]
[ConditionalOnConfig("altruist:server:transport")]
public class RoomSender : IAltruistRouterSender
{
    /// <summary>The connection store rooms are resolved from.</summary>
    protected readonly IConnectionStore _store;
    /// <summary>The packet codec.</summary>
    protected readonly ICodec _codec;
    /// <summary>The per-client sender used for each member.</summary>
    protected readonly ClientSender _clientSender;

    /// <summary>Creates the room sender.</summary>
    /// <param name="store">The connection store.</param>
    /// <param name="codec">The packet codec.</param>
    /// <param name="clientSender">The per-client sender used for each member.</param>
    public RoomSender(IConnectionStore store, ICodec codec, ClientSender clientSender)
    {
        _store = store;
        _codec = codec;
        _clientSender = clientSender;
    }

    /// <summary>
    /// Sends the packet to every connected member of the room (nothing when the room does not exist). Members are sent
    /// to sequentially; in <c>direct</c> mode a socket exception for one member stops the remaining sends.
    /// </summary>
    /// <typeparam name="TPacketBase">The packet type.</typeparam>
    /// <param name="roomId">The room id.</param>
    /// <param name="message">The packet to send.</param>
    public virtual async Task SendAsync<TPacketBase>(string roomId, TPacketBase message) where TPacketBase : IPacketBase
    {
        var connections = await _store.GetConnectionsInRoomAsync(roomId);

        foreach (var (clientId, socket) in connections)
        {
            if (socket != null && socket.IsConnected)
            {
                await _clientSender.SendAsync(clientId, message);
            }
        }
    }
}

/// <summary>
/// Sends a packet to every connected client in the connection store, optionally excluding one. Registered as a
/// singleton when <c>altruist:server:transport</c> is configured; use it through <see cref="IAltruistRouter.Broadcast"/>.
/// For a subset of clients use <see cref="RoomSender"/>.
/// </summary>
[Service]
[ConditionalOnConfig("altruist:server:transport")]
public class BroadcastSender
{
    private readonly IConnectionStore _store;
    private readonly ClientSender _client;

    /// <summary>Creates the broadcast sender.</summary>
    /// <param name="store">The connection store to enumerate clients from.</param>
    /// <param name="clientSender">The per-client sender used for each client.</param>
    public BroadcastSender(IConnectionStore store, ClientSender clientSender)
    {
        _store = store;
        _client = clientSender;
    }

    /// <summary>
    /// Sends the packet to every connected client, sequentially. In <c>direct</c> mode a socket exception for one
    /// client stops the remaining sends.
    /// </summary>
    /// <typeparam name="TPacketBase">The packet type.</typeparam>
    /// <param name="message">The packet to send.</param>
    /// <param name="excludeClientId">A client to skip (typically the sender), or <c>null</c>.</param>
    public async Task SendAsync<TPacketBase>(TPacketBase message, string? excludeClientId = null) where TPacketBase : IPacketBase
    {
        var connections = await _store.GetAllConnectionsAsync();

        foreach (var socket in connections)
        {
            var clientId = socket.ConnectionId;
            if (clientId == excludeClientId)
                continue;

            if (socket != null && socket.IsConnected)
            {
                await _client.SendAsync(clientId, message);
            }
        }
    }
}
