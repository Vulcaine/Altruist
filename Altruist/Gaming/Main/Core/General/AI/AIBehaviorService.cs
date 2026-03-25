/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.ThreeD;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

public interface IAIBehaviorService
{
    void Tick(WorldSnapshot[] snapshots, float dt);
    AIStateMachine? GetStateMachine(string instanceId);
    int ActiveCount { get; }
}

[Service(typeof(IAIBehaviorService))]
[ConditionalOnConfig("altruist:game")]
public sealed class AIBehaviorService : IAIBehaviorService
{
    private readonly Dictionary<string, AIStateMachine> _machines = new();
    private readonly ILogger _logger;
    private readonly IServiceProvider _serviceProvider;
    private uint _tickCounter;
    private bool _discovered;

    public int ActiveCount => _machines.Count;

    public AIBehaviorService(ILoggerFactory loggerFactory, IServiceProvider serviceProvider)
    {
        _logger = loggerFactory.CreateLogger<AIBehaviorService>();
        _serviceProvider = serviceProvider;
    }

    private void EnsureDiscovered()
    {
        if (_discovered) return;
        _discovered = true;

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        AIBehaviorDiscovery.DiscoverBehaviors(
            assemblies,
            type => _serviceProvider.GetService(type),
            _logger);
    }

    public void Tick(WorldSnapshot[] snapshots, float dt)
    {
        EnsureDiscovered();
        _tickCounter++;

        foreach (var snapshot in snapshots)
        {
            var allObjects = snapshot.AllObjects;
            for (int i = 0; i < allObjects.Count; i++)
            {
                var obj = allObjects[i];
                if (obj is not IAIBehaviorEntity aiEntity) continue;
                if (aiEntity.AIContext == null) continue;

                // Skip hibernated entities
                if (obj is IHibernatable { IsHibernated: true }) continue;
                if (obj.Expired) continue;

                // Get or create FSM
                if (!_machines.TryGetValue(obj.InstanceId, out var fsm))
                {
                    fsm = AIBehaviorDiscovery.CreateStateMachine(aiEntity.AIBehaviorName);
                    if (fsm == null) continue;

                    _machines[obj.InstanceId] = fsm;
                    fsm.Initialize(aiEntity.AIContext);
                }

                try
                {
                    fsm.Update(aiEntity.AIContext, dt);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "AI tick failed for {Id}", obj.InstanceId);
                }
            }
        }

        // Periodic cleanup of destroyed entities (every ~4 seconds at 25Hz)
        if (_tickCounter % 100 == 0)
            CleanupDestroyedEntities(snapshots);
    }

    public AIStateMachine? GetStateMachine(string instanceId)
    {
        return _machines.TryGetValue(instanceId, out var fsm) ? fsm : null;
    }

    /// <summary>Remove FSMs for entities no longer in the world.</summary>
    public void RemoveMachine(string instanceId)
    {
        _machines.Remove(instanceId);
    }

    private readonly HashSet<string> _cleanupActiveIds = new();
    private readonly List<string> _cleanupToRemove = new();

    private void CleanupDestroyedEntities(WorldSnapshot[] snapshots)
    {
        _cleanupActiveIds.Clear();
        foreach (var snapshot in snapshots)
        {
            var allObjects = snapshot.AllObjects;
            for (int i = 0; i < allObjects.Count; i++)
            {
                if (allObjects[i] is IAIBehaviorEntity)
                    _cleanupActiveIds.Add(allObjects[i].InstanceId);
            }
        }

        _cleanupToRemove.Clear();
        foreach (var id in _machines.Keys)
        {
            if (!_cleanupActiveIds.Contains(id))
                _cleanupToRemove.Add(id);
        }
        foreach (var id in _cleanupToRemove)
            _machines.Remove(id);
    }
}
