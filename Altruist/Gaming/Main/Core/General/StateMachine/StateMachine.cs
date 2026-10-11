/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Generic FSM driver. One instance per entity. Consumers (AI service, combo
/// system) create one from a shared <see cref="StateMachineDef{TContext}"/>
/// and tick it per entity.
///
/// <para>Lifecycle: build a def once with <see cref="StateMachineBuilder{TContext}"/>, create one
/// machine per entity, call <see cref="Initialize"/> once (runs the initial state's enter hook),
/// then <see cref="Update"/> every tick. Time is in the same unit as the <c>dt</c> you pass
/// (seconds by convention; durations, delays and windows assume it). Not thread-safe; the machine
/// holds no RNG and runs handlers in a fixed order (time advance, delay gate, update, then
/// exit → metadata → <c>OnStateEnter</c> hook → enter), so it is deterministic if the handlers are.</para>
///
/// <para>Choosing: use this for logic with memory (phases, modes, behaviours that persist over
/// ticks). For AI agents use <see cref="AIStateMachine"/> (same driver, plus discovery by
/// <see cref="AIBehaviorAttribute"/>); for a stateless "which rule applies now" choice use
/// <see cref="Flow.FirstMatch{TCtx, TResult}"/>; for scored choices use
/// <see cref="Flow.UtilitySelector{TOption, TCtx}"/>.</para>
/// </summary>
/// <example><code>
/// var def = new StateMachineBuilder&lt;PhaseContext&gt;()
///     .RegisterHandlers&lt;MatchPhases&gt;()          // [State("countdown", Initial = true)], [State("play")], ...
///     .Build();
/// var fsm = new StateMachine&lt;PhaseContext&gt;(def);
/// fsm.Initialize(ctx);
/// // each tick:
/// if (fsm.Update(ctx, dt)) OnPhaseChanged(fsm.CurrentStateName);
/// </code></example>
public class StateMachine<TContext> where TContext : class, IStateContextCore
{
    /// <summary>The immutable definition this machine runs.</summary>
    protected readonly StateMachineDef<TContext> Def;

    /// <summary>Name of the current state.</summary>
    public string CurrentStateName { get; private set; }
    /// <summary>Time since the last transition (sum of <c>dt</c> passed to <see cref="Advance"/>); mirrored to
    /// <see cref="IStateContextCore.TimeInState"/>.</summary>
    public float  TimeInState      { get; private set; }

    /// <summary>Creates a machine positioned on the def's initial state (no hooks run until <see cref="Initialize"/>).</summary>
    /// <exception cref="ArgumentNullException"><paramref name="def"/> is null.</exception>
    public StateMachine(StateMachineDef<TContext> def)
    {
        Def = def ?? throw new ArgumentNullException(nameof(def));
        CurrentStateName = def.InitialState;
    }

    /// <summary>Tick the FSM: <see cref="Advance"/> then <see cref="RunState"/>. Returns true if a
    /// state transition occurred during this call.</summary>
    public bool Update(TContext context, float dt)
    {
        if (!Def.Updates.ContainsKey(CurrentStateName))
            return false;
        Advance(context, dt);
        return RunState(context, dt);
    }

    /// <summary>
    /// The time accounting half of <see cref="Update"/>: records the previous progress, then
    /// <c>TimeInState += dt</c> (float32, the same expression as an inline <c>phaseTime += dt</c>).
    /// Split from <see cref="RunState"/> for simulations that advance the phase clock at the start of
    /// a tick and evaluate the phase's transitions at the end, with other work in between that reads
    /// the clock. A transition (<see cref="TransitionTo"/> or the handler's) resets the time to 0.
    /// </summary>
    public void Advance(TContext context, float dt)
    {
        // Record progress BEFORE advancing time, so WindowEntered/Exited can compare.
        float prevProgress = StateDuration(context) > 0f
            ? TimeInState / StateDuration(context)
            : 0f;
        TimeInState += dt;
        context.TimeInState = TimeInState;
        context.PreviousProgress = prevProgress;
    }

    /// <summary>
    /// The handler half of <see cref="Update"/>: runs the current state's update (unless its delay
    /// has not elapsed) without advancing time, and transitions to the state it returns. Returns true
    /// if a transition occurred. Returning the current state's own name stays (no re-entry); use
    /// <see cref="TransitionTo"/> to re-enter a state (Exit, Enter, time reset).
    /// </summary>
    public bool RunState(TContext context, float dt)
    {
        if (!Def.Updates.TryGetValue(CurrentStateName, out var update))
            return false;

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

    /// <summary>Resets to the initial state and runs its entry (metadata, <c>OnStateEnter</c> hook,
    /// enter hook) without running any exit hook. Call once before the first <see cref="Update"/>;
    /// use <see cref="Reset"/> to restart a running machine.</summary>
    public void Initialize(TContext context)
    {
        CurrentStateName = Def.InitialState;
        TimeInState = 0f;
        context.TimeInState = 0f;
        context.PreviousProgress = 0f;
        ApplyStateMetadata(context, CurrentStateName);
        InvokeOnEnter(context, CurrentStateName);
    }

    /// <summary>Force transition to a specific state (runs Exit + Enter hooks). Transitioning to the
    /// current state re-enters it: Exit, Enter, and the time in state back to 0. Unknown states are
    /// ignored.</summary>
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
