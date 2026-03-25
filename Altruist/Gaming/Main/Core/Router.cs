using Altruist.Engine;
using Altruist.Networking;

namespace Altruist.Gaming;

[Service(typeof(IClientSynchronizator))]
[ConditionalOnConfig("altruist:game")]
public class GameClientSynchronizator : IClientSynchronizator
{
    private readonly BroadcastSender _broadcast;

    public GameClientSynchronizator(BroadcastSender broadcastSender)
    {
        _broadcast = broadcastSender;
    }

    public virtual async Task SendAsync(ISynchronizedEntity entity, bool forceAllAsChanged = false)
    {
        var (changeMasks, maskCount, changedProperties) = Synchronization.GetChangedData(entity, entity.ClientId, AltruistEngine.CurrentTick, forceAllAsChanged);

        bool anyChanges = false;
        for (int i = 0; i < maskCount; i++)
        {
            if (changeMasks[i] != 0) { anyChanges = true; break; }
        }
        System.Buffers.ArrayPool<ulong>.Shared.Return(changeMasks);

        if (!anyChanges)
            return;

        var syncData = new SyncPacket(entity.GetType().Name, changedProperties);
        await _broadcast.SendAsync(syncData);
    }

    public Task SendAsync<TPacketBase>(string clientId, TPacketBase message) where TPacketBase : IPacketBase
    {
        throw new NotImplementedException();
    }
}
