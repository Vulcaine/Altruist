/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Marks a class as an AI behavior. Thin specialization of <see cref="StateBehaviorAttribute"/>;
/// the generic discovery picks it up because it's a <see cref="StateBehaviorAttribute"/> subclass.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class AIBehaviorAttribute : StateBehaviorAttribute
{
    public AIBehaviorAttribute(string name) : base(name) { }
}

/// <summary>
/// Marks a method as an AI state update handler. Thin specialization of <see cref="StateAttribute"/>.
/// Signature: <c>string? MethodName(TContext context, float dt)</c> where <c>TContext : IAIContext</c>.
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class AIStateAttribute : StateAttribute
{
    public AIStateAttribute(string name) : base(name) { }
}

/// <summary>Time unit for the <see cref="StateAttribute.Delay"/> field.</summary>
public enum TimeUnit
{
    Seconds,
    Milliseconds,
}

/// <summary>Marks a method as a one-shot hook invoked once on transition INTO the named state.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class AIStateEnterAttribute : StateEnterAttribute
{
    public AIStateEnterAttribute(string stateName) : base(stateName) { }
}

/// <summary>Marks a method as a one-shot hook invoked once on transition OUT OF the named state.</summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class AIStateExitAttribute : StateExitAttribute
{
    public AIStateExitAttribute(string stateName) : base(stateName) { }
}
