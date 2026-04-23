/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// AI-specific context marker. Extends <see cref="IStateContext"/> so the AI
/// framework reuses the generic state machine core. No AI-specific members here
/// — game code defines the concrete context with whatever fields AI needs
/// (target VID, aggro timers, wander state, …).
/// </summary>
public interface IAIContext : IStateContext
{
}

/// <summary>
/// Implement on world objects that should be ticked by the AI behavior system.
/// The service discovers the matching <see cref="AIBehaviorAttribute"/> by name
/// and auto-ticks the state machine.
/// </summary>
public interface IAIBehaviorEntity
{
    /// <summary>Name of the AI behavior (matches <c>[AIBehavior("name")]</c>).</summary>
    string AIBehaviorName { get; }

    /// <summary>Runtime AI context. Created by game code during spawn.</summary>
    IAIContext AIContext { get; }
}
