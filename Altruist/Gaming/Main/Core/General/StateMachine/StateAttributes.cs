/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Marks a class as a source of state-machine handlers.
/// Subclasses: <c>AIBehaviorAttribute</c>, <c>ComboBehaviorAttribute</c>, etc.
///
/// <para>The attribute itself is only a marker plus a name: the framework's AI discovery scans for
/// <see cref="AIBehaviorAttribute"/>; other domains derive their own subclass and feed the class to
/// <see cref="StateMachineBuilder{TContext}.RegisterHandlers{T}(T)"/>.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public class StateBehaviorAttribute : Attribute
{
    /// <summary>The behavior's registry name.</summary>
    public string Name { get; }

    /// <summary>Names the behavior.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public StateBehaviorAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }
}

/// <summary>
/// Marks a method as a state update handler. Signature:
/// <c>string? MethodName(TContext context, float dt)</c>, where <c>TContext : IStateContextCore</c>.
/// Return the next state name to transition, or null to stay.
///
/// <para>The same method can back multiple states by decorating with <c>[State]</c> multiple times —
/// different <see cref="Name"/> per occurrence. Use <see cref="IStateContextCore.CurrentStateTag"/>
/// or builder-attached per-state data to branch.</para>
///
/// <para>Delay: the update method is not called until <see cref="Delay"/> has elapsed since entry.
/// Enter/Exit hooks still fire immediately.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public class StateAttribute : Attribute
{
    /// <summary>The state's name (the value update handlers return to transition to it).</summary>
    public string  Name    { get; }
    /// <summary>Optional tag exposed as <see cref="IStateContextCore.CurrentStateTag"/> while in this state.</summary>
    public string? Tag     { get; }
    /// <summary>Marks the initial state; if several are marked, the last one registered wins.
    /// <see cref="StateMachineBuilder{TContext}.SetInitial"/> overrides it.</summary>
    public bool    Initial { get; set; }
    /// <summary>Time after entry before the update handler starts being called (in <see cref="DelayUnit"/>; 0 = none).</summary>
    public float   Delay   { get; set; }
    /// <summary>Unit of <see cref="Delay"/> (default seconds).</summary>
    public TimeUnit DelayUnit { get; set; } = TimeUnit.Seconds;

    /// <summary>Declares the method as the update handler of state <paramref name="name"/>.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> is null.</exception>
    public StateAttribute(string name)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
    }

    /// <summary>Declares the method as the update handler of state <paramref name="name"/> with a <paramref name="tag"/>.</summary>
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
    /// <summary>The state whose entry triggers the hook.</summary>
    public string StateName { get; }

    /// <summary>Declares the method as the enter hook of <paramref name="stateName"/>.</summary>
    public StateEnterAttribute(string stateName)
    {
        StateName = stateName ?? throw new ArgumentNullException(nameof(stateName));
    }
}

/// <summary>Marks a method as a one-shot hook called once on transition out of the named state.
/// Signature: <c>void MethodName(TContext context)</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true, Inherited = true)]
public class StateExitAttribute : Attribute
{
    /// <summary>The state whose exit triggers the hook.</summary>
    public string StateName { get; }

    /// <summary>Declares the method as the exit hook of <paramref name="stateName"/>.</summary>
    public StateExitAttribute(string stateName)
    {
        StateName = stateName ?? throw new ArgumentNullException(nameof(stateName));
    }
}

/// <summary>
/// Attaches a normalized window to a state. Multiple windows per state are allowed
/// (e.g. "damage", "input", "cancel"). Read at runtime via
/// <see cref="IStateContextCore.InWindow"/> / <see cref="IStateContextCore.WindowEntered"/> /
/// <see cref="IStateContextCore.WindowExited"/>.
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
    /// <summary>The state the window belongs to.</summary>
    public string StateName { get; }
    /// <summary>Consumer-defined window name ("damage", "input", ...).</summary>
    public string Kind      { get; }
    /// <summary>Normalized start (0..1 of the state's duration).</summary>
    public float  Start     { get; }
    /// <summary>Normalized end (0..1 of the state's duration, inclusive).</summary>
    public float  End       { get; }

    /// <summary>Declares window <paramref name="kind"/> on <paramref name="stateName"/> over normalized
    /// progress <paramref name="start"/>..<paramref name="end"/>. Windows only fire in states with a
    /// duration (see <see cref="StateMachineBuilder{TContext}.StateConfig.Duration"/>).</summary>
    public StateWindowAttribute(string stateName, string kind, float start, float end)
    {
        StateName = stateName ?? throw new ArgumentNullException(nameof(stateName));
        Kind      = kind      ?? throw new ArgumentNullException(nameof(kind));
        Start     = start;
        End       = end;
    }
}
