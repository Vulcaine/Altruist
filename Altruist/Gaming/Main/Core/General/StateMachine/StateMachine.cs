/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Generic FSM driver. One instance per entity. Consumers (AI service, combo
/// system) create one from a shared <see cref="StateMachineDef{TContext}"/>
/// and tick it per entity.
/// </summary>
public class StateMachine<TContext> where TContext : class, IStateContext
{
    protected readonly StateMachineDef<TContext> Def;

    public string CurrentStateName { get; private set; }
    public float  TimeInState      { get; private set; }

    public StateMachine(StateMachineDef<TContext> def)
    {
        Def = def ?? throw new ArgumentNullException(nameof(def));
        CurrentStateName = def.InitialState;
    }

    /// <summary>Tick the FSM. Returns true if a state transition occurred during this call.</summary>
    public bool Update(TContext context, float dt)
    {
        if (!Def.Updates.TryGetValue(CurrentStateName, out var update))
            return false;

        // Record progress BEFORE advancing time, so WindowEntered/Exited can compare.
        float prevProgress = StateDuration(context) > 0f
            ? TimeInState / StateDuration(context)
            : 0f;
        TimeInState += dt;
        context.TimeInState = TimeInState;
        context.PreviousProgress = prevProgress;

        // Delay gate: update method is suppressed until delay elapses. Enter/Exit still ran.
        if (Def.Delays.TryGetValue(CurrentStateName, out var delay) && TimeInState < delay)
            return false;

        var next = update(context, dt);
        if (next != null && next != CurrentStateName && Def.Updates.ContainsKey(next))
        {
            DoTransition(context, next);
            return true;
        }
        return false;
    }

    /// <summary>Force-run initial state entry (called on first registration).</summary>
    public void Initialize(TContext context)
    {
        CurrentStateName = Def.InitialState;
        TimeInState = 0f;
        context.TimeInState = 0f;
        context.PreviousProgress = 0f;
        ApplyStateMetadata(context, CurrentStateName);
        InvokeOnEnter(context, CurrentStateName);
    }

    /// <summary>Force transition to a specific state (runs Exit + Enter hooks).</summary>
    public void TransitionTo(TContext context, string stateName)
    {
        if (!Def.Updates.ContainsKey(stateName)) return;
        DoTransition(context, stateName);
    }

    /// <summary>Reset to initial state, firing the current state's Exit hook first.</summary>
    public void Reset(TContext context)
    {
        if (Def.Exits.TryGetValue(CurrentStateName, out var exit))
            exit?.Invoke(context);
        Initialize(context);
    }

    private void DoTransition(TContext context, string next)
    {
        if (Def.Exits.TryGetValue(CurrentStateName, out var exit))
            exit?.Invoke(context);

        CurrentStateName = next;
        TimeInState = 0f;
        context.TimeInState = 0f;
        context.PreviousProgress = 0f;

        ApplyStateMetadata(context, next);
        InvokeOnEnter(context, next);
    }

    private void ApplyStateMetadata(TContext context, string state)
    {
        context.CurrentStateTag  = Def.Tags.TryGetValue(state, out var tag) ? (tag ?? "") : "";
        context.ActiveWindows    = Def.Windows.TryGetValue(state, out var wins) ? wins : null;
        context.StateDuration    = Def.Durations.TryGetValue(state, out var dur) ? dur : 0f;
        context.CurrentStateData = Def.Data.TryGetValue(state, out var data) ? data : null;
        context.CurrentStateMotion = Def.Motions.TryGetValue(state, out var motion) ? motion : null;
    }

    private void InvokeOnEnter(TContext context, string state)
    {
        // Builder-level transition hook — runs BEFORE the per-state [StateEnter] method.
        // Consumers use it to rewrite StateDuration (e.g. attack-speed scaling).
        if (Def.OnStateEnter != null)
        {
            var baseDur = Def.Durations.TryGetValue(state, out var d) ? d : 0f;
            Def.OnStateEnter(context, state, baseDur);
        }

        // Per-state [StateEnter] method.
        if (Def.Enters.TryGetValue(state, out var enter))
            enter?.Invoke(context);
    }

    private static float StateDuration(TContext context) => context.StateDuration;
}
