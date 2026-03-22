/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;
using Altruist.Gaming.ThreeD;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

public interface IAIBehaviorService
{
    void Tick(IEnumerable<IGameWorldManager3D> worlds, float dt);
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

    public void Tick(IEnumerable<IGameWorldManager3D> worlds, float dt)
    {
        EnsureDiscovered();
        _tickCounter++;

        foreach (var world in worlds)
        {
            foreach (var obj in world.FindAllObjects<IWorldObject3D>())
            {
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
            CleanupDestroyedEntities(worlds);
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

    private void CleanupDestroyedEntities(IEnumerable<IGameWorldManager3D> worlds)
    {
        var activeIds = new HashSet<string>();
        foreach (var world in worlds)
        {
            foreach (var obj in world.FindAllObjects<IWorldObject3D>())
            {
                if (obj is IAIBehaviorEntity)
                    activeIds.Add(obj.InstanceId);
            }
        }

        var toRemove = _machines.Keys.Where(id => !activeIds.Contains(id)).ToList();
        foreach (var id in toRemove)
            _machines.Remove(id);
    }
}
