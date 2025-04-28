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

namespace Altruist;

public interface IAltruistRouterSender
{
    Task SendAsync<TPacketBase>(string clientId, TPacketBase message) where TPacketBase : IPacketBase;
}

public interface IAltruistRouter
{
    ClientSender Client { get; }
    RoomSender Room { get; }
    BroadcastSender Broadcast { get; }
    ClientSynchronizator Synchronize { get; }

}


public abstract class AbstractAltruistRouter : IAltruistRouter
{
    protected readonly IConnectionStore _connectionStore;
    protected readonly ICodec _codec;

    public ClientSender Client { get; }

    public RoomSender Room { get; }

    public BroadcastSender Broadcast { get; }

    public ClientSynchronizator Synchronize { get; }

    public AbstractAltruistRouter(IConnectionStore store, ICodec codec, ClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, ClientSynchronizator clientSynchronizator)
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
    protected DirectRouter(IConnectionStore store, ICodec codec, ClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, ClientSynchronizator clientSynchronizator) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator)
    {
    }
}

public class ClientSender : IAltruistRouterSender
{
    protected readonly IConnectionStore _store;
    protected readonly ICodec _codec;

    public ClientSender(IConnectionStore store, ICodec codec)
    {
        _store = store;
        _codec = codec;
    }

    public virtual async Task SendAsync(string clientId, byte[] message)
    {
        var socket = await _store.GetConnectionAsync(clientId);
        if (socket != null && socket.IsConnected)
        {
            await socket.SendAsync(message);
        }
    }

    public virtual async Task SendAsync<TPacketBase>(string clientId, TPacketBase message) where TPacketBase : IPacketBase
    {
        var encodedMessage = _codec.Encoder.Encode(message);
        await SendAsync(clientId, encodedMessage);
    }
}

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

public class ClientSynchronizator
{
    private readonly BroadcastSender _broadcast;

    public ClientSynchronizator(BroadcastSender broadcastSender)
    {
        _broadcast = broadcastSender;
    }

    public virtual async Task SendAsync(ISynchronizedEntity entity, bool forceAllAsChanged = false)
    {
        var (changeMasks, changedProperties) = Synchronization.GetChangedData(entity, entity.ConnectionId, forceAllAsChanged);

        bool anyChanges = changeMasks.Any(mask => mask != 0);
        if (!anyChanges)
            return;

        var syncData = new SyncPacket("server", entity.GetType().Name, changedProperties);
        await _broadcast.SendAsync(syncData);
    }

}

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

        foreach (var (clientId, socket) in connections)
        {
            if (clientId == excludeClientId)
                continue;

            message.Header.SetReceiver(clientId);
            if (socket != null && socket.IsConnected)
            {
                await _client.SendAsync(clientId, message);
            }
        }
    }
}
