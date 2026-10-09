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
    // The behavior type each named template was built from.
    private static readonly Dictionary<string, Type> _templateTypes = new();
    // Templates of CreateStateMachine<TBehavior>(), by type: a behavior always runs its own
    // handlers, even when another type registered the same name.
    private static readonly Dictionary<Type, StateMachineDef<IAIContext>> _byType = new();
    private static readonly object _lock = new();
    private static bool _discovered;

    /// <summary>
    /// Builds a template for every <see cref="AIBehaviorAttribute"/> class in
    /// <paramref name="assemblies"/>. Runs once per process (later calls return immediately); a
    /// behavior that cannot be created or whose handlers have a wrong signature is logged and skipped.
    /// A later class registering the same name replaces the earlier one.
    /// </summary>
    /// <param name="assemblies">Assemblies to scan.</param>
    /// <param name="instanceFactory">Resolves a behavior instance (typically from DI); when it returns
    /// null or throws, a parameterless constructor is tried.</param>
    /// <param name="logger">Receives discovery diagnostics.</param>
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
                lock (_lock)
                {
                    _templates[attr.Name] = def;
                    _templateTypes[attr.Name] = type;
                }
                logger.LogInformation("[AI-DISC] Registered AI behavior '{Name}' with states: [{States}]",
                    attr.Name, string.Join(", ", def.Updates.Keys));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[AI-DISC] Failed to build AI behavior {Name} from {Type}", attr.Name, type.FullName);
            }
        }
    }

    /// <summary>Create a new FSM instance from a registered behavior template, or null when no
    /// behavior is registered under <paramref name="behaviorName"/>. The caller must
    /// <see cref="StateMachine{TContext}.Initialize"/> it before the first update.</summary>
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
    /// test). The behavior is built once per type (parameterless constructor; behaviors keep
    /// their per-agent state in the context), or reuses the template the scan built from this
    /// same type. It is registered under its <see cref="AIBehaviorAttribute"/> name, so
    /// <see cref="CreateStateMachine(string)"/> finds it too, unless another type already holds
    /// that name: the name keeps its first registration, and this method still runs
    /// <typeparamref name="TBehavior"/>'s handlers.
    /// </summary>
    public static AIStateMachine CreateStateMachine<TBehavior>() where TBehavior : class, new()
    {
        var type = typeof(TBehavior);
        var name = type.GetCustomAttribute<AIBehaviorAttribute>()?.Name
            ?? throw new InvalidOperationException($"{type.Name} has no [AIBehavior] attribute.");
        StateMachineDef<IAIContext>? def;
        lock (_lock)
        {
            if (!_byType.TryGetValue(type, out def)
                && _templateTypes.TryGetValue(name, out var registered) && registered == type
                && _templates.TryGetValue(name, out def))
                _byType[type] = def;
        }
        def ??= Build(type, new TBehavior());
        lock (_lock)
        {
            if (_byType.TryGetValue(type, out var cached)) def = cached;
            else _byType[type] = def;
            if (!_templates.ContainsKey(name))
            {
                _templates[name] = def;
                _templateTypes[name] = type;
            }
        }
        return new AIStateMachine(def);
    }

    /// <summary>True when a behavior template is registered under <paramref name="name"/> (by the scan or
    /// by <see cref="CreateStateMachine{TBehavior}"/>).</summary>
    public static bool HasBehavior(string name)
    {
        lock (_lock) return _templates.ContainsKey(name);
    }

    private static StateMachineDef<IAIContext> Build(Type type, object instance) =>
        new StateMachineBuilder<IAIContext>()
            .RegisterHandlers(type, instance)
            .Build();
}
