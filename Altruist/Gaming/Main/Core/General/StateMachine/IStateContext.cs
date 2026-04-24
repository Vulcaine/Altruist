/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Normalized [start, end] window within a state's duration.
/// Used for damage/input/cancel/recovery windows in combo states —
/// generic so any consumer (quests, skills, …) can define their own "kind" names.
/// </summary>
public readonly struct StateWindow
{
    public readonly float Start;
    public readonly float End;

    public StateWindow(float start, float end)
    {
        Start = start;
        End = end;
    }
}

/// <summary>
/// Base context for any state machine consumer. Holds the mutable per-tick values
/// the framework needs to drive transitions and expose progress helpers.
///
/// <para>The framework sets <see cref="TimeInState"/>, <see cref="StateDuration"/>,
/// <see cref="CurrentStateTag"/>, <see cref="ActiveWindows"/>, and
/// <see cref="PreviousProgress"/> — consumer code only reads them.</para>
///
/// <para>Consumers implement this via a domain-specific context interface:
/// <c>IAIContext : IStateContext</c>, <c>IComboContext : IStateContext</c>, etc.</para>
/// </summary>
public interface IStateContext
{
    /// <summary>The entity this context belongs to. Back-reference for state logic.</summary>
    ITypelessWorldObject Entity { get; }

    /// <summary>Seconds spent in the current state since the last transition. Set by the driver each tick.</summary>
    float TimeInState { get; set; }

    /// <summary>Base duration (seconds) of the current state. Set by the driver on entry from the builder's <c>Duration</c>.
    /// 0 = open-ended (no normalized progress, windows never fire). Builder <c>OnStateEnter</c> hooks can override.</summary>
    float StateDuration { get; set; }

    /// <summary>Optional tag string the user attached via <see cref="StateAttribute.Tag"/>.
    /// Populated on every transition — user code reads it when a single handler backs many states.</summary>
    string CurrentStateTag { get; set; }

    /// <summary>Active windows for the current state (kind → [start, end]). Set by the driver on transition.
    /// Null if the state has no windows. Read via the helpers below.</summary>
    IReadOnlyDictionary<string, StateWindow>? ActiveWindows { get; set; }

    /// <summary>Opaque per-state payload attached via the builder's <c>.Data(...)</c>.
    /// Set by the driver on every transition. Consumers cast to the expected type.</summary>
    object? CurrentStateData { get; set; }

    /// <summary>Optional first-class motion profile attached to the active state.
    /// The driver swaps this on every transition from the builder's state metadata.</summary>
    StateMotionProfile? CurrentStateMotion { get; set; }

    /// <summary>Progress value recorded at the previous tick; used by window-edge helpers.
    /// Set by the driver to the prior <see cref="Progress"/> before each state update.</summary>
    float PreviousProgress { get; set; }

    /// <summary>Normalized progress through the current state: <c>TimeInState / StateDuration</c>.
    /// Returns 0 for open-ended states.</summary>
    float Progress => StateDuration > 0f ? TimeInState / StateDuration : 0f;

    /// <summary>True while <see cref="Progress"/> is within the named window. False for open-ended states.</summary>
    bool InWindow(string kind)
    {
        if (StateDuration <= 0f) return false;
        if (ActiveWindows == null) return false;
        if (!ActiveWindows.TryGetValue(kind, out var w)) return false;
        float p = Progress;
        return p >= w.Start && p <= w.End;
    }

    /// <summary>Edge-triggered: true only on the tick where <see cref="Progress"/> first crossed the window start.</summary>
    bool WindowEntered(string kind)
    {
        if (StateDuration <= 0f) return false;
        if (ActiveWindows == null) return false;
        if (!ActiveWindows.TryGetValue(kind, out var w)) return false;
        return PreviousProgress < w.Start && Progress >= w.Start;
    }

    /// <summary>Edge-triggered: true only on the tick where <see cref="Progress"/> just passed the window end.</summary>
    bool WindowExited(string kind)
    {
        if (StateDuration <= 0f) return false;
        if (ActiveWindows == null) return false;
        if (!ActiveWindows.TryGetValue(kind, out var w)) return false;
        return PreviousProgress <= w.End && Progress > w.End;
    }
}
