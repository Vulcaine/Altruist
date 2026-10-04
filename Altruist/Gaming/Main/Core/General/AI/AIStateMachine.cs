/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// AI-specific state machine — a thin specialization of <see cref="StateMachine{TContext}"/>
/// over <see cref="IAIContext"/>. Exists only to keep the <c>AIStateMachine</c> type name
/// public-facing; all behavior lives in the generic base.
/// </summary>
public sealed class AIStateMachine : StateMachine<IAIContext>
{
    public AIStateMachine(StateMachineDef<IAIContext> def) : base(def) { }
}
