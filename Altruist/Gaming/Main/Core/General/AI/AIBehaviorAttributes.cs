/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Marks a class as an AI behavior definition.
/// The class contains [AIState] methods that define state machine logic.
/// Discovered at startup and compiled into FSM templates.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AIBehaviorAttribute : Attribute
{
    public string Name { get; }

    public AIBehaviorAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
}

/// <summary>
/// Marks a method as a state update handler in an [AIBehavior] class.
/// Signature: string? MethodName(TContext context, float dt) where TContext : IAIContext
/// Return next state name to transition, or null to stay in current state.
///
/// The framework tracks TimeInState on the context (IAIContext.TimeInState),
/// which resets to 0 on every transition. Use it for timed states:
///   if (ctx.TimeInState >= 5f) return "Idle";
///
/// Delay: the Update method is not called until Delay seconds have elapsed
/// after entering the state. Enter/Exit hooks still fire immediately.
///   [AIState("Talk", Delay = 2f)] — waits 2 seconds before first Update call
///   [AIState("Patrol", Delay = 500, DelayUnit = TimeUnit.Milliseconds)]
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AIStateAttribute : Attribute
{
    public string Name { get; }
    public bool Initial { get; set; }

    /// <summary>
    /// Delay before the state's Update method starts running after entry.
    /// Default unit is seconds. Use DelayUnit to change.
    /// </summary>
    public float Delay { get; set; }

    /// <summary>Unit for Delay value. Default: Seconds.</summary>
    public TimeUnit DelayUnit { get; set; } = TimeUnit.Seconds;

    public AIStateAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
}

public enum TimeUnit
{
    Seconds,
    Milliseconds,
}

/// <summary>
/// Marks a method as a state enter hook. Called once when transitioning into this state.
/// Signature: void MethodName(TContext context)
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AIStateEnterAttribute : Attribute
{
    public string StateName { get; }

    public AIStateEnterAttribute(string stateName)
    {
        StateName = stateName ?? throw new ArgumentNullException(nameof(stateName));
    }
}

/// <summary>
/// Marks a method as a state exit hook. Called once when transitioning out of this state.
/// Signature: void MethodName(TContext context)
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class AIStateExitAttribute : Attribute
{
    public string StateName { get; }

    public AIStateExitAttribute(string stateName)
    {
        StateName = stateName ?? throw new ArgumentNullException(nameof(stateName));
    }
}
