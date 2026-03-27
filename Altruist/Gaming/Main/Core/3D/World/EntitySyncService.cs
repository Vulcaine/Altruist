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
/// Ticked by GameWorldOrganizer3D — no game code needed.
/// Uses spatial broadcast for visibility-aware sync — entities only sync to
/// players who can see them.
/// </summary>
[Service(typeof(IEntitySyncService))]
[ConditionalOnConfig("altruist:game")]
public sealed class EntitySyncService : IEntitySyncService
{
    private readonly IVisibilityTracker? _visibilityTracker;
    private readonly ClientSender? _clientSender;
    private readonly BroadcastSender? _broadcastSender;
    private readonly ILogger _logger;
    private uint _tickCounter;

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
                if (string.IsNullOrEmpty(syncEntity.ClientId)) continue;

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
        using var changes = Synchronization.GetSyncChanges(
            entity, entity.ClientId, AltruistEngine.CurrentTick);

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

public interface IEntitySyncService
{
    Task Tick(WorldSnapshot[] snapshots, float engineFrequencyHz);
}
