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

[Service]
[ConditionalOnConfig("altruist:server:transport")]
public class ClientSender : IAltruistRouterSender
{
    protected readonly IConnectionStore _store;
    protected readonly ICodec _codec;
    protected readonly IDashboardNetworkRecorder? _networkRecorder;

    public ClientSender(IConnectionStore store, ICodec codec, IDashboardNetworkRecorder? networkRecorder = null)
    {
        _store = store;
        _codec = codec;
        _networkRecorder = networkRecorder;
    }

    public virtual async Task SendAsync(string clientId, byte[] message)
    {
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

    protected virtual async Task RecordOutboundAsync(
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
            return;

        string? roomId = null;
        try
        {
            roomId = (await _store.FindRoomForClientAsync(clientId))?.Id;
        }
        catch
        {
            roomId = null;
        }

        await _networkRecorder.RecordAsync(new DashboardNetworkEvent
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
