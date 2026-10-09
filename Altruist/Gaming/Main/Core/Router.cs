using Altruist.Engine;
using Altruist.Networking;

namespace Altruist.Gaming;

/// <summary>
/// The gaming module's <see cref="IClientSynchronizator"/> (used through <see cref="IAltruistRouter.Synchronize"/>):
/// computes the changed <c>[Synced]</c> properties of an <see cref="ISynchronizedEntity"/> for the current
/// engine tick (<see cref="AltruistEngine.CurrentTick"/>) and broadcasts them as one <see cref="SyncPacket"/>
/// to every connected client. Registered as a singleton when <c>altruist:game</c> is configured, replacing
/// the core placeholder that throws.
/// <para>
/// When to use: simple delta sync of a few shared entities to everyone. It is not interest-managed: for
/// per-client visibility in persistent worlds use the spatial broadcast / visibility services of the world
/// organizers, and for authoritative match rooms send the game's own snapshots through
/// <c>RoomHost</c> (Altruist.Gaming.Rooms).
/// </para>
/// </summary>
[Service(typeof(IClientSynchronizator))]
[ConditionalOnConfig("altruist:game")]
public class GameClientSynchronizator : IClientSynchronizator
{
    private readonly BroadcastSender _broadcast;

    /// <summary>Created by DI.</summary>
    /// <param name="broadcastSender">Sends to every connected client.</param>
    public GameClientSynchronizator(BroadcastSender broadcastSender)
    {
        _broadcast = broadcastSender;
    }

    /// <inheritdoc/>
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

    /// <summary>Not supported: always throws. Send to one client with <see cref="IAltruistRouter.Client"/> instead.</summary>
    /// <typeparam name="TPacketBase">The packet type.</typeparam>
    /// <param name="clientId">Unused.</param>
    /// <param name="message">Unused.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="NotImplementedException">Always.</exception>
    public Task SendAsync<TPacketBase>(string clientId, TPacketBase message) where TPacketBase : IPacketBase
    {
        throw new NotImplementedException();
    }
}
