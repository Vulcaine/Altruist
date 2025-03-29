using Altruist.Networking;

namespace Altruist;

public interface IAltruistRouterSender
{
    Task SendAsync<TPacketBase>(string clientId, TPacketBase message) where TPacketBase : IPacketBase;
}

public interface IAltruistRouter
{
    ClientSender Client {get;}
    RoomSender Room {get;}
    BroadcastSender Broadcast {get;}
    ClientSynchronizator Synchronize{get;}

}

public interface IAltruistEngineRouter : IAltruistRouter {}

public abstract class AbstractAltruistRouter : IAltruistRouter
{
    protected readonly IConnectionStore _connectionStore;
    protected readonly IMessageEncoder _encoder;

    public ClientSender Client {get;}

    public RoomSender Room {get;}

    public BroadcastSender Broadcast {get;}

    public ClientSynchronizator Synchronize {get;}

    public AbstractAltruistRouter(IConnectionStore store, IMessageEncoder encoder, ClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, ClientSynchronizator clientSynchronizator)
    {
        _connectionStore = store;
        _encoder = encoder;

        Client = clientSender;
        Room = roomSender;
        Broadcast = broadcastSender;
        Synchronize = clientSynchronizator;
    }
}

public abstract class DirectRouter : AbstractAltruistRouter
{
    protected DirectRouter(IConnectionStore store, IMessageEncoder encoder, ClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, ClientSynchronizator clientSynchronizator) : base(store, encoder, clientSender, roomSender, broadcastSender, clientSynchronizator)
    {
    }
}

public abstract class EngineRouter : AbstractAltruistRouter, IAltruistEngineRouter
{
    private readonly IAltruistEngine _engine;

    protected EngineRouter(IConnectionStore store, IMessageEncoder encoder, EngineClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, ClientSynchronizator clientSynchronizator, IAltruistEngine engine) : base(store, encoder, clientSender, roomSender, broadcastSender, clientSynchronizator)
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
    protected readonly IMessageEncoder _encoder;

    public ClientSender(IConnectionStore store, IMessageEncoder encoder)
    {
        _store = store;
        _encoder = encoder;
    }

    public virtual async Task SendAsync<TPacketBase>(string clientId, TPacketBase message) where TPacketBase : IPacketBase
    {
        var socket = await _store.GetConnection(clientId);
        var encodedMessage = _encoder.Encode(message);

        if (socket != null && socket.IsConnected)
        {
            await socket.SendAsync(encodedMessage);
        }
    }
}

public class EngineClientSender : ClientSender {
    private readonly IAltruistEngine _engine;
    public EngineClientSender(IConnectionStore store, IMessageEncoder encoder, IAltruistEngine engine) : base(store, encoder)
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
    protected readonly IMessageEncoder _encoder;
    protected readonly ClientSender _clientSender;

    public RoomSender(IConnectionStore store, IMessageEncoder encoder, ClientSender clientSender)
    {
        _store = store;
        _encoder = encoder;
        _clientSender = clientSender;
    }

    public virtual async Task SendAsync<TPacketBase>(string roomId, TPacketBase message) where TPacketBase : IPacketBase
    {
        var connections = await _store.GetConnectionsInRoom(roomId);

        foreach (var (clientId, socket) in connections)
        {
            if (socket != null && socket.IsConnected)
            {
                await _clientSender.SendAsync(clientId,message);
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
