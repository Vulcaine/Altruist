/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Immutable snapshot of a state machine: dispatch tables, per-state metadata
/// (duration, tag, windows, user data), and the optional transition hook.
/// Produced by <see cref="StateMachineBuilder{TContext}.Build"/>; consumed by
/// <see cref="StateMachine{TContext}"/>.
/// </summary>
public sealed class StateMachineDef<TContext> where TContext : class, IStateContext
{
    public string InitialState { get; }
    public IReadOnlyDictionary<string, Func<TContext, float, string?>> Updates { get; }
    public IReadOnlyDictionary<string, Action<TContext>> Enters { get; }
    public IReadOnlyDictionary<string, Action<TContext>> Exits { get; }
    public IReadOnlyDictionary<string, float> Delays { get; }
    public IReadOnlyDictionary<string, float> Durations { get; }
    public IReadOnlyDictionary<string, string?> Tags { get; }
    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, StateWindow>> Windows { get; }
    public IReadOnlyDictionary<string, object?> Data { get; }

    /// <summary>Consumer-supplied hook invoked on every state entry as
    /// <c>(context, stateName, baseDuration)</c>. Useful for per-domain
    /// transformations — e.g. combo scales <c>context.StateDuration</c> by the
    /// player's attack speed.</summary>
    public Action<TContext, string, float>? OnStateEnter { get; }

    internal StateMachineDef(
        string initialState,
        IReadOnlyDictionary<string, Func<TContext, float, string?>> updates,
        IReadOnlyDictionary<string, Action<TContext>> enters,
        IReadOnlyDictionary<string, Action<TContext>> exits,
        IReadOnlyDictionary<string, float> delays,
        IReadOnlyDictionary<string, float> durations,
        IReadOnlyDictionary<string, string?> tags,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, StateWindow>> windows,
        IReadOnlyDictionary<string, object?> data,
        Action<TContext, string, float>? onStateEnter)
    {
        InitialState = initialState;
        Updates = updates;
        Enters = enters;
        Exits = exits;
        Delays = delays;
        Durations = durations;
        Tags = tags;
        Windows = windows;
        Data = data;
        OnStateEnter = onStateEnter;
    }

    /// <summary>Get the per-state user-data blob attached via the builder's <c>.Data(…)</c> (or null).</summary>
    public object? GetData(string stateName)
        => Data.TryGetValue(stateName, out var d) ? d : null;
}
