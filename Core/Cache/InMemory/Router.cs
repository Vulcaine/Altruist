namespace Altruist.InMemory;

public class InMemoryDirectRouter : DirectRouter
{
    public InMemoryDirectRouter(IConnectionStore store, IMessageEncoder encoder, ClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, ClientSynchronizator clientSynchronizator) : base(store, encoder, clientSender, roomSender, broadcastSender, clientSynchronizator)
    {
    }
}

public class InMemoryEngineRouter : EngineRouter
{
    public InMemoryEngineRouter(IConnectionStore store, IMessageEncoder encoder, EngineClientSender clientSender, RoomSender roomSender, BroadcastSender broadcastSender, ClientSynchronizator clientSynchronizator, IAltruistEngine engine) : base(store, encoder, clientSender, roomSender, broadcastSender, clientSynchronizator, engine)
    {
    }
}

// public class InMemoryRouter : IAltruistRouter
// {
//     private readonly InMemoryEngineRouter _engineRouter;
//     private readonly InMemoryDirectRouter _directRouter;
//     private readonly bool _engineEnabled;

//     public InMemoryRouter(InMemoryDirectRouter directRouter, InMemoryEngineRouter engineRouter, IAltruistContext altruistContext)
//     {
//         _engineRouter = engineRouter;
//         _directRouter = directRouter;
//         _engineEnabled = altruistContext.EngineEnabled;
//     }

//     public ConnectionClientSender Client(string clientId)
//     {
//         return _engineEnabled ? _engineRouter.Client(clientId) : _directRouter.Client(clientId);
//     }

//     public RoomSender Room(string roomId)
//     {
//         return _engineEnabled ? _engineRouter.Room(roomId) : _directRouter.Room(roomId);
//     }

//     public BroadcastSender Except(string clientId)
//     {
//         return _engineEnabled ? _engineRouter.Except(clientId) : _directRouter.Except(clientId);
//     }

//     public ClientSynchronizator Sync(ISynchronizedEntity entity)
//     {
//         return _engineEnabled ? _engineRouter.Sync(entity) : _directRouter.Sync(entity);
//     }
// }