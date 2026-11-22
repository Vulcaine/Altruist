/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Marks a class as a named AI behavior: a set of <see cref="AIStateAttribute"/> /
/// <see cref="AIStateEnterAttribute"/> / <see cref="AIStateExitAttribute"/> handler methods that
/// <see cref="AIBehaviorDiscovery"/> compiles into one shared <see cref="StateMachineDef{TContext}"/>
/// template. Thin specialization of <see cref="StateBehaviorAttribute"/>.
///
/// <para>Two ways to run it: (1) world objects implementing <see cref="IAIBehaviorEntity"/> whose
/// <see cref="IAIBehaviorEntity.AIBehaviorName"/> equals this name are ticked automatically by
/// <see cref="AIBehaviorService"/> (the class is found by an assembly scan at startup and resolved
/// from DI, falling back to a parameterless constructor); (2) agents that their owner ticks itself
/// (a bot seat in a match room, a headless simulation, a test) call
/// <see cref="AIBehaviorDiscovery.CreateStateMachine{TBehavior}"/> and drive the returned
/// <see cref="AIStateMachine"/>. The behavior instance is shared by every agent: keep per-agent
/// state in the context, not in fields.</para>
///
/// <para>Choosing: use an AI behavior for agent decision-making with persistent states. For
/// non-AI state machines (combos, quests, UI flows) use <see cref="StateMachineBuilder{TContext}"/>
/// with plain <see cref="StateAttribute"/>; for stateless per-tick choices use
/// <see cref="Flow.FirstMatch{TCtx, TResult}"/> or <see cref="Flow.UtilitySelector{TOption, TCtx}"/>.</para>
/// </summary>
/// <example><code>
/// public sealed class GuardContext : AIContext { public string? TargetId; }
///
/// [AIBehavior("guard")]
/// public sealed class GuardBehavior
/// {
///     [AIState("idle", Initial = true)]
///     string? Idle(GuardContext ctx, float dt) =&gt; ctx.TargetId != null ? "chase" : null;
///
///     [AIState("chase")]
///     string? Chase(GuardContext ctx, float dt) =&gt; ctx.TargetId == null ? "idle" : null;
///
///     [AIStateEnter("chase")]
///     void OnChase(GuardContext ctx) { /* e.g. play an alert */ }
/// }
///
/// // Self-ticked agent:
/// var fsm = AIBehaviorDiscovery.CreateStateMachine&lt;GuardBehavior&gt;();
/// var ctx = new GuardContext();
/// fsm.Initialize(ctx);
/// fsm.Update(ctx, dt);   // every tick
/// </code></example>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AIBehaviorAttribute : StateBehaviorAttribute
{
    /// <summary>Declares the behavior under <paramref name="name"/> (the key matched against
    /// <see cref="IAIBehaviorEntity.AIBehaviorName"/>; unique per process).</summary>
    public AIBehaviorAttribute(string name) : base(name) { }
}

/// <summary>
/// Marks a method as an AI state update handler. Thin specialization of <see cref="StateAttribute"/>
/// (same options: <see cref="StateAttribute.Initial"/>, <see cref="StateAttribute.Delay"/>, repeatable to back several states).
/// Signature: <c>string? MethodName(TContext context, float dt)</c> where <c>TContext : IAIContext</c>;
/// <c>dt</c> is in seconds; return the next state's name, or null to stay.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class AIStateAttribute : StateAttribute
{
    /// <summary>Declares the method as the update handler of state <paramref name="name"/>.</summary>
    public AIStateAttribute(string name) : base(name) { }
}

/// <summary>Time unit for the <see cref="StateAttribute.Delay"/> field.</summary>
public enum TimeUnit
{
    /// <summary>The delay is in seconds (default).</summary>
    Seconds,
    /// <summary>The delay is in milliseconds (divided by 1000 when the machine is built).</summary>
    Milliseconds,
}

/// <summary>Marks a method as a one-shot hook invoked once on transition INTO the named state
/// (also for the initial state on <see cref="StateMachine{TContext}.Initialize"/>).
/// Signature: <c>void MethodName(TContext context)</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class AIStateEnterAttribute : StateEnterAttribute
{
    /// <summary>Declares the method as the enter hook of state <paramref name="stateName"/>.</summary>
    public AIStateEnterAttribute(string stateName) : base(stateName) { }
}

/// <summary>Marks a method as a one-shot hook invoked once on transition OUT OF the named state.
/// Signature: <c>void MethodName(TContext context)</c>.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class AIStateExitAttribute : StateExitAttribute
{
    /// <summary>Declares the method as the exit hook of state <paramref name="stateName"/>.</summary>
    public AIStateExitAttribute(string stateName) : base(stateName) { }
}
