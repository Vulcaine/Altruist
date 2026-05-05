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

        var asmList = assemblies.ToList();
        var behaviorTypes = TypeDiscovery.FindTypesWithAttribute<AIBehaviorAttribute>(asmList).ToList();
        logger.LogInformation("[AI-DISC] scanning {Asm} assemblies, found {N} [AIBehavior] types",
            asmList.Count, behaviorTypes.Count);

        foreach (var type in behaviorTypes)
        {
            var attr = type.GetCustomAttribute<AIBehaviorAttribute>()!;

            try
            {
                object? instance;
                try
                {
                    instance = instanceFactory(type);
                }
                catch (Exception ex)
                {
                    logger.LogWarning("[AI-DISC] DI factory threw for {Type}: {ExType}: {ExMessage}\n{Stack}",
                        type.FullName, ex.GetType().FullName, ex.Message, ex.ToString());
                    instance = null;
                }

                if (instance == null)
                {
                    try
                    {
                        instance = Activator.CreateInstance(type);
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "[AI-DISC] Activator.CreateInstance failed for {Type} — likely missing parameterless ctor (constructor injects services that DI couldn't resolve)", type.FullName);
                        continue;
                    }
                }

                if (instance == null)
                {
                    logger.LogWarning("[AI-DISC] Could not create AI behavior instance {Type}", type.FullName);
                    continue;
                }

                var def = new StateMachineBuilder<IAIContext>()
                    .RegisterHandlers(type, instance)
                    .Build();

                _templates[attr.Name] = def;
                logger.LogInformation("[AI-DISC] Registered AI behavior '{Name}' with states: [{States}]",
                    attr.Name, string.Join(", ", def.Updates.Keys));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[AI-DISC] Failed to build AI behavior {Name} from {Type}", attr.Name, type.FullName);
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
