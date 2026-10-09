/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using Altruist.Engine;
using Altruist.Networking;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

/// <summary>
/// Automatically synchronizes all [Synchronized] ISynchronizedEntity world objects.
/// Ticked by GameWorldOrganizer3D (and the 2D organizer) — no game code needed.
/// Uses spatial broadcast for visibility-aware sync — entities only sync to
/// players who can see them.
/// </summary>
/// <remarks>
/// Default singleton <see cref="IEntitySyncService"/> whenever <c>altruist:game</c> is configured (2D and 3D).
/// Use it for persistent-world entities; match rooms send their own snapshots instead.
/// </remarks>
[Service(typeof(IEntitySyncService))]
[ConditionalOnConfig("altruist:game")]
public sealed class EntitySyncService : IEntitySyncService
{
    private readonly IVisibilityTracker? _visibilityTracker;
    private readonly ClientSender? _clientSender;
    private readonly BroadcastSender? _broadcastSender;
    private readonly ILogger _logger;
    private uint _tickCounter;

    /// <summary>Creates the service; without a <see cref="ClientSender"/> or <see cref="BroadcastSender"/> it does nothing.</summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="visibilityTracker">Optional tracker; when present, changes are sent to every observer of the entity.</param>
    /// <param name="clientSender">Per-client sender (preferred delivery path).</param>
    /// <param name="broadcastSender">Fallback: broadcast to all clients when no <paramref name="clientSender"/> is available.</param>
    public EntitySyncService(
        ILoggerFactory loggerFactory,
        IVisibilityTracker? visibilityTracker = null,
        ClientSender? clientSender = null,
        BroadcastSender? broadcastSender = null)
    {
        _visibilityTracker = visibilityTracker;
        _clientSender = clientSender;
        _broadcastSender = broadcastSender;
        _logger = loggerFactory.CreateLogger<EntitySyncService>();
    }

    private static readonly ConcurrentDictionary<Type, SynchronizedAttribute?> _syncAttrCache = new();

    /// <inheritdoc/>
    public async Task Tick(WorldSnapshot[] snapshots, float engineFrequencyHz)
    {
        if (_clientSender == null && _broadcastSender == null) return;

        _tickCounter++;

        foreach (var snapshot in snapshots)
        {
            var allObjects = snapshot.AllObjects;
            for (int i = 0; i < allObjects.Count; i++)
            {
                var obj = allObjects[i];
                if (obj is not ISynchronizedEntity syncEntity) continue;
                // AI-controlled entities (monsters, NPCs) intentionally have an
                // empty ClientId — they're not network observers — but their
                // [Synced] state still needs to reach the players observing them
                // through the visibility tracker. Skip only when there is no
                // delivery path at all: no self-send target AND no observer broadcast.
                if (string.IsNullOrEmpty(syncEntity.ClientId) && _visibilityTracker == null) continue;

                var entityType = obj.GetType();
                var syncAttr = _syncAttrCache.GetOrAdd(entityType, static t =>
                    (SynchronizedAttribute?)Attribute.GetCustomAttribute(t, typeof(SynchronizedAttribute)));
                if (syncAttr == null) continue;

                if (syncAttr.Frequency > 0 && !ShouldSync(syncAttr, engineFrequencyHz))
                    continue;

                try
                {
                    await SendSyncData(syncEntity, obj);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Sync failed for {Id}", syncEntity.ClientId);
                }
            }
        }
    }

    private async Task SendSyncData(ISynchronizedEntity entity, ITypelessWorldObject worldObj)
    {
        // Delta cache must be keyed per-entity. Using entity.ClientId here
        // collides for AI entities (monsters/NPCs) because they all share
        // an empty ClientId — last-value tracking would be overwritten by
        // every other monster on the same tick, producing garbage diffs.
        // The world object's InstanceId is the stable per-entity identifier.
        using var changes = Synchronization.GetSyncChanges(
            entity, worldObj.InstanceId, AltruistEngine.CurrentTick);

        if (!changes.HasChanges) return;

        var syncData = new SyncPacket(entity.GetType().Name, changes.Data);

        if (_clientSender != null)
        {
            // Visibility-aware: send to players who can see this entity
            if (_visibilityTracker != null)
            {
                foreach (var observerClientId in _visibilityTracker.GetObserversOf(worldObj.InstanceId))
                {
                    await _clientSender.SendAsync(observerClientId, syncData);
                }
            }

            // Player entities: send to their own TCP client (self-sync)
            // AI entities (monsters/NPCs): only sync via visibility, no self-send
            if (worldObj is not IAIBehaviorEntity && !string.IsNullOrEmpty(entity.ClientId))
            {
                await _clientSender.SendAsync(entity.ClientId, syncData);
            }
        }
        else if (_broadcastSender != null)
        {
            await _broadcastSender.SendAsync(syncData);
        }
    }

    private bool ShouldSync(SynchronizedAttribute attr, float engineHz)
    {
        uint interval = attr.Unit switch
        {
            SyncUnit.Ticks => (uint)attr.Frequency,
            SyncUnit.Hz => engineHz > 0 ? (uint)(engineHz / attr.Frequency) : 1,
            SyncUnit.Seconds => (uint)(engineHz * attr.Frequency),
            _ => 1,
        };

        return interval == 0 || (_tickCounter % interval) == 0;
    }
}

/// <summary>
/// Per-tick automatic delta sync of world objects marked with <see cref="Altruist.Networking.SynchronizedAttribute"/>.
/// Ticked by the world organizers (<see cref="Altruist.Gaming.ThreeD.GameWorldOrganizer3D"/> and the 2D counterpart);
/// game code normally never calls it. Replace the registration to customize delivery.
/// </summary>
public interface IEntitySyncService
{
    /// <summary>
    /// Scans every object in <paramref name="snapshots"/> that implements <see cref="Altruist.Networking.ISynchronizedEntity"/>
    /// and carries <see cref="Altruist.Networking.SynchronizedAttribute"/>, and sends a <c>SyncPacket</c> with its changed
    /// <c>[Synced]</c> properties when its sync interval is due. Delivery: to each observer from the visibility tracker, plus
    /// self-sync to the owning <c>ClientId</c> for non-AI entities; or a broadcast to all clients when only a broadcast sender exists.
    /// </summary>
    /// <remarks>
    /// Delta state is keyed per <c>InstanceId</c> and engine tick. Intervals are counted in calls to this method (one per world
    /// step), using <paramref name="engineFrequencyHz"/> to convert <c>Hz</c>/<c>Seconds</c> units into a call count.
    /// Send failures are logged and do not stop the tick.
    /// </remarks>
    /// <param name="snapshots">Per-world object snapshots for this tick.</param>
    /// <param name="engineFrequencyHz">Assumed step rate in Hz (config <c>altruist:game:worlds:entity-sync-hz</c>, default 25).</param>
    Task Tick(WorldSnapshot[] snapshots, float engineFrequencyHz);
}
