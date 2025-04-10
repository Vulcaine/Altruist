namespace Altruist.InMemory;

public class InMemoryDirectRouter : DirectRouter
{
    public InMemoryDirectRouter(IConnectionStore store, ICodec codec, ClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, ClientSynchronizator clientSynchronizator) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator)
    {
    }
}

public class InMemoryEngineRouter : EngineRouter
{
    public InMemoryEngineRouter(IConnectionStore store, ICodec codec, EngineClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, ClientSynchronizator clientSynchronizator, IAltruistEngine engine) : base(store, codec, clientSender, roomSender, broadcastSender, clientSynchronizator, engine)
    {
    }
}
