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

public interface IAltruistEngineRouter : IAltruistRouter { }

public abstract class AbstractAltruistRouter : IAltruistRouter
{
    protected readonly IConnectionStore _connectionStore;
    protected readonly IMessageCodec _codec;

    public ClientSender Client { get; }

    public RoomSender Room { get; }

    public BroadcastSender Broadcast { get; }

    public ClientSynchronizator Synchronize { get; }

    public AbstractAltruistRouter(IConnectionStore store, IMessageCodec codec, ClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, ClientSynchronizator clientSynchronizator)
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
    protected DirectRouter(IConnectionStore store, IMessageCodec codec, ClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, ClientSynchronizator clientSynchronizator) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator)
    {
    }
}

public abstract class EngineRouter : AbstractAltruistRouter, IAltruistEngineRouter
{
    private readonly IAltruistEngine _engine;

    protected EngineRouter(IConnectionStore store, IMessageCodec codec, EngineClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, ClientSynchronizator clientSynchronizator, IAltruistEngine engine) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator)
    {
        _engine = engine;
    }

    public virtual void SendTask(TaskIdentifier taskIdentifier, Delegate task)
    {
        _engine.SendTask(taskIdentifier, task);
    }
}

public class ClientSender : IAltruistRouterSender
{
    protected readonly IConnectionStore _store;
    protected readonly IMessageCodec _codec;

    public ClientSender(IConnectionStore store, IMessageCodec codec)
    {
        _store = store;
        _codec = codec;
    }

    public virtual async Task SendAsync<TPacketBase>(string clientId, TPacketBase message) where TPacketBase : IPacketBase
    {
        var socket = await _store.GetConnection(clientId);
        var encodedMessage = _codec.Encoder.Encode(message);

        if (socket != null && socket.IsConnected)
        {
            await socket.SendAsync(encodedMessage);
        }
    }
}

public class EngineClientSender : ClientSender
{
    private readonly IAltruistEngine _engine;
    public EngineClientSender(IConnectionStore store, IMessageCodec codec, IAltruistEngine engine) : base(store, codec)
    {
        _engine = engine;
    }

    public override Task SendAsync<TPacketBase>(string clientId, TPacketBase message)
    {
        _engine.SendTask(new TaskIdentifier(clientId + message.Type), () => base.SendAsync(clientId, message));
        return Task.CompletedTask;
    }
}

public class RoomSender : IAltruistRouterSender
{
    protected readonly IConnectionStore _store;
    protected readonly IMessageCodec _codec;
    protected readonly ClientSender _clientSender;

    public RoomSender(IConnectionStore store, IMessageCodec codec, ClientSender clientSender)
    {
        _store = store;
        _codec = codec;
        _clientSender = clientSender;
    }

    public virtual async Task SendAsync<TPacketBase>(string roomId, TPacketBase message) where TPacketBase : IPacketBase
    {
        var connections = await _store.GetConnectionsInRoom(roomId);

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

    public async Task SendAsync(ISynchronizedEntity entity)
    {
        var (changeMask, changedProperties) = Synchronization.GetChangedData(entity, entity.ConnectionId);
        if (changeMask == 0)
            return;

        var syncData = new SyncPacket("server", entity.GetType().Name, changedProperties);
        await _broadcast.SendAsync(syncData, entity.ConnectionId);
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
        var connections = await _store.GetAllConnections();

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
