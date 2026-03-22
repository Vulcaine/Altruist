/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Framework-managed finite state machine. Created automatically from
/// [AIBehavior] classes by AIBehaviorDiscovery. One instance per entity.
/// </summary>
public sealed class AIStateMachine
{
    public string CurrentStateName { get; private set; }

    /// <summary>Seconds spent in the current state since last transition.</summary>
    public float TimeInState { get; private set; }

    private readonly Dictionary<string, Func<IAIContext, float, string?>> _updateMethods;
    private readonly Dictionary<string, Action<IAIContext>?> _enterMethods;
    private readonly Dictionary<string, Action<IAIContext>?> _exitMethods;
    private readonly Dictionary<string, float> _delays;
    private readonly string _initialState;

    internal AIStateMachine(
        string initialState,
        Dictionary<string, Func<IAIContext, float, string?>> updateMethods,
        Dictionary<string, Action<IAIContext>?> enterMethods,
        Dictionary<string, Action<IAIContext>?> exitMethods,
        Dictionary<string, float> delays)
    {
        _initialState = initialState;
        _updateMethods = updateMethods;
        _enterMethods = enterMethods;
        _exitMethods = exitMethods;
        _delays = delays;
        CurrentStateName = initialState;
    }

    /// <summary>Tick the FSM. Returns true if a state transition occurred.</summary>
    public bool Update(IAIContext context, float dt)
    {
        if (!_updateMethods.TryGetValue(CurrentStateName, out var update))
            return false;

        TimeInState += dt;
        context.TimeInState = TimeInState;

        // Delay: skip Update calls until delay period has elapsed
        if (_delays.TryGetValue(CurrentStateName, out var delay) && TimeInState < delay)
            return false;

        // Run the state's update method
        var nextState = update(context, dt);
        if (nextState != null && nextState != CurrentStateName
            && _updateMethods.ContainsKey(nextState))
        {
            DoTransition(context, nextState);
            return true;
        }

        return false;
    }

    /// <summary>Force-enter the initial state (called on first registration).</summary>
    internal void Initialize(IAIContext context)
    {
        CurrentStateName = _initialState;
        TimeInState = 0;
        if (_enterMethods.TryGetValue(_initialState, out var enter))
            enter?.Invoke(context);
    }

    /// <summary>Force transition to a specific state.</summary>
    public void TransitionTo(IAIContext context, string stateName)
    {
        if (!_updateMethods.ContainsKey(stateName)) return;
        DoTransition(context, stateName);
    }

    /// <summary>Reset to initial state.</summary>
    public void Reset(IAIContext context)
    {
        if (_exitMethods.TryGetValue(CurrentStateName, out var exit))
            exit?.Invoke(context);
        Initialize(context);
    }

    private void DoTransition(IAIContext context, string nextState)
    {
        if (_exitMethods.TryGetValue(CurrentStateName, out var exit))
            exit?.Invoke(context);

        CurrentStateName = nextState;
        TimeInState = 0;

        if (_enterMethods.TryGetValue(CurrentStateName, out var enter))
            enter?.Invoke(context);
    }
}

