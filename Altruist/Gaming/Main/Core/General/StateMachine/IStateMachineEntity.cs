/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>
/// Optional: implement on world objects that should be auto-ticked by a
/// generic state-machine driver service. AI and combo both use specialized
/// services today, so this interface is here for future consumers (e.g.
/// quest state machines) and is not required for AI/combo to function.
/// </summary>
public interface IStateMachineEntity<TContext> where TContext : class, IStateContext
{
    /// <summary>Name of the behavior (matches <c>[StateBehavior("name")]</c>).</summary>
    string BehaviorName { get; }

    /// <summary>Runtime context. Game code supplies it during spawn.</summary>
    TContext Context { get; }
}
