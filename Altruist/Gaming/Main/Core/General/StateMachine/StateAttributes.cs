/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Marks a class as a source of state-machine handlers.
/// Subclasses: <c>AIBehaviorAttribute</c>, <c>ComboBehaviorAttribute</c>, etc.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public class StateBehaviorAttribute : Attribute
{
    public string Name { get; }

    public StateBehaviorAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
}

/// <summary>
/// Marks a method as a state update handler. Signature:
/// <c>string? MethodName(TContext context, float dt)</c>, where <c>TContext : IStateContext</c>.
/// Return the next state name to transition, or null to stay.
///
/// <para>The same method can back multiple states by decorating with <c>[State]</c> multiple times —
/// different <see cref="Name"/> per occurrence. Use <see cref="IStateContext.CurrentStateTag"/>
/// or builder-attached per-state data to branch.</para>
///
/// <para>Delay: the update method is not called until <see cref="Delay"/> has elapsed since entry.
/// Enter/Exit hooks still fire immediately.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public class StateAttribute : Attribute
{
    public string  Name    { get; }
    public string? Tag     { get; }
    public bool    Initial { get; set; }
    public float   Delay   { get; set; }
    public TimeUnit DelayUnit { get; set; } = TimeUnit.Seconds;

    public StateAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    public StateAttribute(string name, string? tag) : this(name)
    {
        Tag = tag;
    }
}

/// <summary>Marks a method as a one-shot hook called once on transition into the named state.
/// Signature: <c>void MethodName(TContext context)</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public class StateEnterAttribute : Attribute
{
    public string StateName { get; }

    public StateEnterAttribute(string stateName)
    {
        StateName = stateName ?? throw new ArgumentNullException(nameof(stateName));
    }
}

/// <summary>Marks a method as a one-shot hook called once on transition out of the named state.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public class StateExitAttribute : Attribute
{
    public string StateName { get; }

    public StateExitAttribute(string stateName)
    {
        StateName = stateName ?? throw new ArgumentNullException(nameof(stateName));
    }
}

/// <summary>
/// Attaches a normalized window to a state. Multiple windows per state are allowed
/// (e.g. "damage", "input", "cancel"). Read at runtime via
/// <see cref="IStateContext.InWindow"/> / <see cref="IStateContext.WindowEntered"/> /
/// <see cref="IStateContext.WindowExited"/>.
///
/// <para>Applied to the SAME method as the <see cref="StateAttribute"/>:</para>
/// <code>
/// [State("attack1")]
/// [StateWindow("attack1", "damage", 0.45f, 0.60f)]
/// [StateWindow("attack1", "input",  0.60f, 1.00f)]
/// string? Attack1(IComboContext ctx, float dt) { ... }
/// </code>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public class StateWindowAttribute : Attribute
{
    public string StateName { get; }
    public string Kind      { get; }
    public float  Start     { get; }
    public float  End       { get; }

    public StateWindowAttribute(string stateName, string kind, float start, float end)
    {
        StateName = stateName ?? throw new ArgumentNullException(nameof(stateName));
        Kind      = kind      ?? throw new ArgumentNullException(nameof(kind));
        Start     = start;
        End       = end;
    }
}
