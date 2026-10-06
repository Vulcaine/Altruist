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
    private static readonly object _lock = new();
    private static bool _discovered;

    public static void DiscoverBehaviors(
        IEnumerable<Assembly> assemblies,
        Func<Type, object?> instanceFactory,
        ILogger logger)
    {
        lock (_lock)
        {
            if (_discovered) return;
            _discovered = true;
        }

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

                var def = Build(type, instance);
                lock (_lock) _templates[attr.Name] = def;
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
        lock (_lock)
            return _templates.TryGetValue(behaviorName, out var def)
                ? new AIStateMachine(def)
                : null;
    }

    /// <summary>
    /// A new FSM of the <typeparamref name="TBehavior"/> behavior, without an assembly scan:
    /// for agents their owner ticks itself (a bot in a match room, a headless simulation, a
    /// test). The behavior is built once (parameterless constructor; behaviors keep their
    /// per-agent state in the context) and registered under its <see cref="AIBehaviorAttribute"/>
    /// name, so <see cref="CreateStateMachine(string)"/> finds it too.
    /// </summary>
    public static AIStateMachine CreateStateMachine<TBehavior>() where TBehavior : class, new()
    {
        var name = typeof(TBehavior).GetCustomAttribute<AIBehaviorAttribute>()?.Name
            ?? throw new InvalidOperationException($"{typeof(TBehavior).Name} has no [AIBehavior] attribute.");
        StateMachineDef<IAIContext>? def;
        lock (_lock) _templates.TryGetValue(name, out def);
        if (def is null)
        {
            var built = Build(typeof(TBehavior), new TBehavior());
            lock (_lock)
            {
                if (!_templates.TryGetValue(name, out def)) _templates[name] = def = built;
            }
        }
        return new AIStateMachine(def);
    }

    public static bool HasBehavior(string name)
    {
        lock (_lock) return _templates.ContainsKey(name);
    }

    private static StateMachineDef<IAIContext> Build(Type type, object instance) =>
        new StateMachineBuilder<IAIContext>()
            .RegisterHandlers(type, instance)
            .Build();
}
