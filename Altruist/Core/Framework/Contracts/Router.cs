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

public interface IAltruistRouterSender
{
    Task SendAsync<TPacketBase>(string clientId, TPacketBase message) where TPacketBase : IPacketBase;
}

public interface IClientSynchronizator
{
    Task SendAsync(ISynchronizedEntity entity, bool forceAllAsChanged = false);
}

public interface IAltruistRouter
{
    ClientSender Client { get; }
    RoomSender Room { get; }
    BroadcastSender Broadcast { get; }
    IClientSynchronizator Synchronize { get; }
}

[Service(typeof(IClientSynchronizator))]
[ConditionalOnConfig("altruist:server:transport")]
public class ClientSynchronizator : IClientSynchronizator
{
    public Task SendAsync(ISynchronizedEntity entity, bool forceAllAsChanged = false)
    {
        throw new NotImplementedException($"ClientSynchronizator.SendAsync() is not implemented. Only working with a gaming module.");
    }
}

public abstract class AbstractAltruistRouter : IAltruistRouter
{
    protected readonly IConnectionStore _connectionStore;
    protected readonly ICodec _codec;

    public ClientSender Client { get; }

    public RoomSender Room { get; }

    public BroadcastSender Broadcast { get; }

    public IClientSynchronizator Synchronize { get; }

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

public abstract class DirectRouter : AbstractAltruistRouter
{
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
    protected readonly IConnectionStore _store;
    protected readonly ICodec _codec;
    protected readonly IDashboardNetworkRecorder? _networkRecorder;
    private OutboundQueues? _outbound;

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

[Service]
[ConditionalOnConfig("altruist:server:transport")]
public class RoomSender : IAltruistRouterSender
{
    protected readonly IConnectionStore _store;
    protected readonly ICodec _codec;
    protected readonly ClientSender _clientSender;

    public RoomSender(IConnectionStore store, ICodec codec, ClientSender clientSender)
    {
        _store = store;
        _codec = codec;
        _clientSender = clientSender;
    }

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

[Service]
[ConditionalOnConfig("altruist:server:transport")]
public class BroadcastSender
{
    private readonly IConnectionStore _store;
    private readonly ClientSender _client;

    public BroadcastSender(IConnectionStore store, ClientSender clientSender)
    {
        _store = store;
        _client = clientSender;
    }

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
