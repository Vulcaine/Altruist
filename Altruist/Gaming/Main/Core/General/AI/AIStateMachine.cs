/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// AI-specific state machine — a thin specialization of <see cref="StateMachine{TContext}"/>
/// over <see cref="IAIContext"/>. Exists only to keep the <c>AIStateMachine</c> type name
/// public-facing; all behavior lives in the generic base.
///
/// <para>Choosing: get one from <see cref="AIBehaviorDiscovery.CreateStateMachine{TBehavior}"/>
/// (self-ticked agents) or let <see cref="AIBehaviorService"/> create and tick it for world objects
/// implementing <see cref="IAIBehaviorEntity"/>. Use the generic
/// <see cref="StateMachine{TContext}"/> directly for non-AI flows (combos, match phases, quests)
/// whose context is not an <see cref="IAIContext"/>.</para>
/// </summary>
public sealed class AIStateMachine : StateMachine<IAIContext>
{
    /// <summary>Creates a machine over a shared, immutable behavior template; the machine itself
    /// holds only the current state and time in state (one per agent). Call
    /// <see cref="StateMachine{TContext}.Initialize"/> before the first update.</summary>
    public AIStateMachine(StateMachineDef<IAIContext> def) : base(def) { }
}
