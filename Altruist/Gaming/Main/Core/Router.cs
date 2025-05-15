using Altruist.Gaming.Engine;
using Altruist.Networking;

namespace Altruist.Gaming;

[Service(typeof(IClientSynchronizator))]
public class GameClientSynchronizator : IClientSynchronizator
{
    private readonly BroadcastSender _broadcast;

    public GameClientSynchronizator(BroadcastSender broadcastSender)
    {
        _broadcast = broadcastSender;
    }

    public virtual async Task SendAsync(ISynchronizedEntity entity, bool forceAllAsChanged = false)
    {
        var (changeMasks, changedProperties) = Synchronization.GetChangedData(entity, entity.ConnectionId, AltruistEngine.CurrentTick, forceAllAsChanged);

        bool anyChanges = changeMasks.Any(mask => mask != 0);
        if (!anyChanges || changedProperties.Count == 0)
            return;

        var safeCopy = changedProperties.ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        var syncData = new SyncPacket("server", entity.GetType().Name, safeCopy);
        await _broadcast.SendAsync(syncData);
    }

    public Task SendAsync<TPacketBase>(string clientId, TPacketBase message) where TPacketBase : IPacketBase
    {
        throw new NotImplementedException();
    }
}
