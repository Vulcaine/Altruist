/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Marker for AI context objects. Game code defines the concrete type
/// with whatever fields the AI needs (target, timers, spawn position, etc.).
/// </summary>
public interface IAIContext
{
    ITypelessWorldObject Entity { get; }

    /// <summary>
    /// Seconds spent in the current FSM state. Set by the framework each tick.
    /// Use in [AIState] methods for time-based logic without manual timer tracking.
    /// </summary>
    float TimeInState { get; set; }
}

/// <summary>
/// Implement on world objects that should be ticked by the AI behavior system.
/// The framework discovers the matching [AIBehavior] by name and auto-ticks the FSM.
/// </summary>
public interface IAIBehaviorEntity
{
    /// <summary>Name of the AI behavior (matches [AIBehavior("name")]).</summary>
    string AIBehaviorName { get; }

    /// <summary>Runtime AI context. Created by game code during spawn.</summary>
    IAIContext AIContext { get; }
}
