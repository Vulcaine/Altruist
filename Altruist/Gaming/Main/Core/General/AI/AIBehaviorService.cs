/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

/// <summary>
/// Ticks the AI state machines of every world object that implements <see cref="IAIBehaviorEntity"/>.
/// Driven by the world organizer (<c>GameWorldOrganizer2D</c> / <c>GameWorldOrganizer3D</c>) once per
/// world step, on the engine thread; game code normally only reads from it.
///
/// <para>Choosing: this is for AI on persistent-world objects. Agents outside a world (bots in a match
/// room, headless simulations) create their own machine with
/// <see cref="AIBehaviorDiscovery.CreateStateMachine{TBehavior}"/> and tick it in their simulation step.</para>
/// </summary>
public interface IAIBehaviorService
{
    /// <summary>Updates the state machine of every AI entity in <paramref name="snapshots"/> by
    /// <paramref name="dt"/> seconds, creating (and initialising) machines for new entities and
    /// skipping hibernated or expired ones. Not thread-safe: call from the world step only.</summary>
    void Tick(WorldSnapshot[] snapshots, float dt);
    /// <summary>The machine of the world object with this instance id, or null if it has not been ticked yet
    /// (or was cleaned up after the object left the world).</summary>
    AIStateMachine? GetStateMachine(string instanceId);
    /// <summary>Number of live state machines (one per ticked AI entity).</summary>
    int ActiveCount { get; }
}

/// <summary>
/// Default <see cref="IAIBehaviorService"/>: one <see cref="AIStateMachine"/> per entity instance id,
/// created lazily from the templates <see cref="AIBehaviorDiscovery"/> builds. Registered as a service
/// only when the <c>altruist:game</c> config section exists. Exceptions thrown by a behavior are logged
/// and swallowed per entity; machines of objects no longer in any snapshot are dropped every 100 ticks.
/// </summary>
[Service(typeof(IAIBehaviorService))]
[ConditionalOnConfig("altruist:game")]
public sealed class AIBehaviorService : IAIBehaviorService
{
    private readonly Dictionary<string, AIStateMachine> _machines = new();
    private readonly ILogger _logger;
    private uint _tickCounter;

    /// <inheritdoc/>
    public int ActiveCount => _machines.Count;

    /// <summary>Created by DI; behaviors are discovered later, in <see cref="DiscoverBehaviors"/>.</summary>
    public AIBehaviorService(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<AIBehaviorService>();
    }

    // Discovery has to wait until [PostConstruct] for two reasons:
    //  1. The DI graph contains a cycle (GameWorldOrganizer3D → AIBehaviorService
    //     → AggressiveMonsterBehavior → CharacterService → GameWorldOrganizer3D),
    //     so we can't resolve behaviors during the AIBehaviorService constructor.
    //  2. An IServiceProvider injected at construction may belong to a temp
    //     bootstrap container (BindConfigurationClasses / BootstrapServices use
    //     `using var tmpProvider`) — by the time we'd need it, that container
    //     is disposed and GetService throws ObjectDisposedException.
    // Pulling the live root provider via Dependencies.RootProvider at PostConstruct
    // time gives us the long-lived bootstrap provider that hosts the runtime
    // singleton graph.
    /// <summary>
    /// Scans all loaded assemblies for <see cref="AIBehaviorAttribute"/> classes and builds their
    /// templates (resolving each behavior from the root DI provider). Invoked once by the container
    /// after construction (<c>[PostConstruct]</c>); later calls are no-ops.
    /// </summary>
    /// <exception cref="InvalidOperationException"><c>Dependencies.RootProvider</c> is not set yet.</exception>
    [PostConstruct]
    public void DiscoverBehaviors()
    {
        var provider = Dependencies.RootProvider
            ?? throw new InvalidOperationException(
                "Dependencies.RootProvider is null at PostConstruct time — "
                + "Bootstrap should have set it before invoking PostConstruct hooks.");

        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        AIBehaviorDiscovery.DiscoverBehaviors(
            assemblies,
            type => provider.GetService(type),
            _logger);
    }

    /// <inheritdoc/>
    public void Tick(WorldSnapshot[] snapshots, float dt)
    {
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

    /// <inheritdoc/>
    public AIStateMachine? GetStateMachine(string instanceId)
    {
        return _machines.TryGetValue(instanceId, out var fsm) ? fsm : null;
    }

    /// <summary>Drops the machine of one entity right away (otherwise removed by the periodic cleanup
    /// once the entity is gone from the world). A later tick of the same id starts a fresh machine.</summary>
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
