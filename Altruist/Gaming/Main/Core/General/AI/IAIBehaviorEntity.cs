/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// AI-specific context marker over the generic state machine core. No AI-specific members
/// here — game code defines the concrete context with whatever fields AI needs (target VID,
/// aggro timers, wander state, …). Contexts of world objects usually also expose the entity
/// (<see cref="IStateContext"/>); agents outside the world (a bot in a match room) need not.
/// </summary>
public interface IAIContext : IStateContextCore
{
}

/// <summary>
/// Convenience base for AI contexts: the bookkeeping members the state machine writes.
/// Derive and add the agent's own state.
/// </summary>
public abstract class AIContext : IAIContext
{
    /// <inheritdoc/>
    public float TimeInState { get; set; }
    /// <inheritdoc/>
    public float StateDuration { get; set; }
    /// <inheritdoc/>
    public string CurrentStateTag { get; set; } = "";
    /// <inheritdoc/>
    public IReadOnlyDictionary<string, StateWindow>? ActiveWindows { get; set; }
    /// <inheritdoc/>
    public object? CurrentStateData { get; set; }
    /// <inheritdoc/>
    public StateMotionProfile? CurrentStateMotion { get; set; }
    /// <inheritdoc/>
    public float PreviousProgress { get; set; }
}

/// <summary>
/// Implement on world objects that should be ticked by the AI behavior system.
/// The service discovers the matching <see cref="AIBehaviorAttribute"/> by name
/// and auto-ticks the state machine.
///
/// <para>Each engine tick <see cref="AIBehaviorService"/> walks the world snapshots, lazily creates
/// one <see cref="AIStateMachine"/> per <see cref="ITypelessWorldObject.InstanceId"/> (initialised on
/// creation), and updates it unless the object is hibernated (<see cref="IHibernatable"/>), expired,
/// or its <see cref="IAIBehaviorEntity.AIContext"/> is still null. Agents that are not world objects (a bot in a match
/// room) should not implement this; create their machine with
/// <see cref="AIBehaviorDiscovery.CreateStateMachine{TBehavior}"/> and tick it yourself.</para>
/// </summary>
public interface IAIBehaviorEntity
{
    /// <summary>Name of the AI behavior (matches <c>[AIBehavior("name")]</c>).</summary>
    string AIBehaviorName { get; }

    /// <summary>Runtime AI context. Created by game code during spawn.</summary>
    IAIContext AIContext { get; }
}
