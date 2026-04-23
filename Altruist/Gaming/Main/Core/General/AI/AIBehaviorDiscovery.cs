/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

/// <summary>
/// Discovers <see cref="AIBehaviorAttribute"/> classes and builds a
/// <see cref="StateMachineDef{IAIContext}"/> per behavior via
/// <see cref="StateMachineBuilder{IAIContext}"/>. Called once at startup from
/// <see cref="AIBehaviorService"/>.
///
/// <para>The heavy lifting (reflection scan, expression-tree dispatch) lives in the
/// generic <see cref="StateMachineBuilder{TContext}"/> — this class is just the
/// AI-specific entry point and template registry.</para>
/// </summary>
public static class AIBehaviorDiscovery
{
    private static readonly Dictionary<string, StateMachineDef<IAIContext>> _templates = new();
    private static bool _discovered;

    public static void DiscoverBehaviors(
        IEnumerable<Assembly> assemblies,
        Func<Type, object?> instanceFactory,
        ILogger logger)
    {
        if (_discovered) return;
        _discovered = true;

        var behaviorTypes = TypeDiscovery.FindTypesWithAttribute<AIBehaviorAttribute>(assemblies);

        foreach (var type in behaviorTypes)
        {
            var attr = type.GetCustomAttribute<AIBehaviorAttribute>()!;
            var instance = instanceFactory(type) ?? Activator.CreateInstance(type);

            if (instance == null)
            {
                logger.LogWarning("Could not create AI behavior instance {Type}", type.FullName);
                continue;
            }

            try
            {
                var def = new StateMachineBuilder<IAIContext>()
                    .RegisterHandlers(type, instance)
                    .Build();

                _templates[attr.Name] = def;
                logger.LogInformation("Registered AI behavior '{Name}' with states: [{States}]",
                    attr.Name, string.Join(", ", def.Updates.Keys));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to build AI behavior {Name} from {Type}", attr.Name, type.FullName);
            }
        }
    }

    /// <summary>Create a new FSM instance from a registered behavior template.</summary>
    public static AIStateMachine? CreateStateMachine(string behaviorName)
    {
        return _templates.TryGetValue(behaviorName, out var def)
            ? new AIStateMachine(def)
            : null;
    }

    public static bool HasBehavior(string name) => _templates.ContainsKey(name);
}
