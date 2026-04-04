/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Linq.Expressions;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming;

/// <summary>
/// Discovers all [AIBehavior] classes and compiles their [AIState] methods
/// into state machine templates. Called once at startup.
/// </summary>
public static class AIBehaviorDiscovery
{
    private static readonly Dictionary<string, BehaviorTemplate> _templates = new();
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

            var template = BuildTemplate(instance, type, logger);
            if (template == null) continue;

            _templates[attr.Name] = template;
            logger.LogInformation("Registered AI behavior '{Name}' with states: [{States}]",
                attr.Name, string.Join(", ", template.StateNames));
        }
    }

    /// <summary>Create a new FSM instance from a behavior template.</summary>
    public static AIStateMachine? CreateStateMachine(string behaviorName)
    {
        return _templates.TryGetValue(behaviorName, out var template)
            ? template.Build()
            : null;
    }

    public static bool HasBehavior(string name) => _templates.ContainsKey(name);

    private static BehaviorTemplate? BuildTemplate(object instance, Type type, ILogger logger)
    {
        var updateMethods = new Dictionary<string, Func<IAIContext, float, string?>>();
        var enterMethods = new Dictionary<string, Action<IAIContext>?>();
        var exitMethods = new Dictionary<string, Action<IAIContext>?>();
        var delays = new Dictionary<string, float>();
        string? initialState = null;

        // Discover [AIState] methods
        var stateMethods = TypeDiscovery.FindInstanceMethodsWithAttribute<AIStateAttribute>(type);
        foreach (var (method, attr) in stateMethods)
        {
            var pars = method.GetParameters();
            if (pars.Length != 2 || !typeof(IAIContext).IsAssignableFrom(pars[0].ParameterType) || pars[1].ParameterType != typeof(float))
            {
                logger.LogWarning("AIState method {Type}.{Method} must have signature (TContext, float) where TContext : IAIContext",
                    type.Name, method.Name);
                continue;
            }

            if (method.ReturnType != typeof(string) && Nullable.GetUnderlyingType(method.ReturnType) == null
                && method.ReturnType != typeof(string))
            {
                // Allow string? return type
            }

            updateMethods[attr.Name] = BuildUpdateInvoker(instance, method, pars[0].ParameterType);

            if (attr.Delay > 0)
            {
                float delaySec = attr.DelayUnit == TimeUnit.Milliseconds
                    ? attr.Delay / 1000f
                    : attr.Delay;
                delays[attr.Name] = delaySec;
            }

            if (attr.Initial)
                initialState = attr.Name;
        }

        // Discover [AIStateEnter] methods
        var enterMethodsList = TypeDiscovery.FindInstanceMethodsWithAttribute<AIStateEnterAttribute>(type);
        foreach (var (method, attr) in enterMethodsList)
        {
            var pars = method.GetParameters();
            if (pars.Length != 1 || !typeof(IAIContext).IsAssignableFrom(pars[0].ParameterType))
            {
                logger.LogWarning("AIStateEnter method {Type}.{Method} must have signature (TContext)",
                    type.Name, method.Name);
                continue;
            }
            enterMethods[attr.StateName] = BuildLifecycleInvoker(instance, method, pars[0].ParameterType);
        }

        // Discover [AIStateExit] methods
        var exitMethodsList = TypeDiscovery.FindInstanceMethodsWithAttribute<AIStateExitAttribute>(type);
        foreach (var (method, attr) in exitMethodsList)
        {
            var pars = method.GetParameters();
            if (pars.Length != 1 || !typeof(IAIContext).IsAssignableFrom(pars[0].ParameterType))
            {
                logger.LogWarning("AIStateExit method {Type}.{Method} must have signature (TContext)",
                    type.Name, method.Name);
                continue;
            }
            exitMethods[attr.StateName] = BuildLifecycleInvoker(instance, method, pars[0].ParameterType);
        }

        if (updateMethods.Count == 0)
        {
            logger.LogWarning("AI behavior {Type} has no [AIState] methods", type.Name);
            return null;
        }

        initialState ??= updateMethods.Keys.First();

        return new BehaviorTemplate(initialState, updateMethods, enterMethods, exitMethods, delays);
    }

    /// <summary>
    /// Build Func&lt;IAIContext, float, string?&gt; that casts context to concrete type and calls the method.
    /// </summary>
    private static Func<IAIContext, float, string?> BuildUpdateInvoker(
        object target, MethodInfo method, Type contextType)
    {
        var targetConst = Expression.Constant(target);
        var ctxParam = Expression.Parameter(typeof(IAIContext), "ctx");
        var dtParam = Expression.Parameter(typeof(float), "dt");
        var castCtx = Expression.Convert(ctxParam, contextType);
        var call = Expression.Call(targetConst, method, castCtx, dtParam);
        var lambda = Expression.Lambda<Func<IAIContext, float, string?>>(call, ctxParam, dtParam);
        return lambda.Compile();
    }

    /// <summary>
    /// Build Action&lt;IAIContext&gt; that casts context and calls enter/exit method.
    /// </summary>
    private static Action<IAIContext> BuildLifecycleInvoker(
        object target, MethodInfo method, Type contextType)
    {
        var targetConst = Expression.Constant(target);
        var ctxParam = Expression.Parameter(typeof(IAIContext), "ctx");
        var castCtx = Expression.Convert(ctxParam, contextType);
        var call = Expression.Call(targetConst, method, castCtx);
        var lambda = Expression.Lambda<Action<IAIContext>>(call, ctxParam);
        return lambda.Compile();
    }

    internal sealed class BehaviorTemplate
    {
        private readonly string _initialState;
        private readonly Dictionary<string, Func<IAIContext, float, string?>> _updateMethods;
        private readonly Dictionary<string, Action<IAIContext>?> _enterMethods;
        private readonly Dictionary<string, Action<IAIContext>?> _exitMethods;
        private readonly Dictionary<string, float> _delays;

        public IEnumerable<string> StateNames => _updateMethods.Keys;

        public BehaviorTemplate(
            string initialState,
            Dictionary<string, Func<IAIContext, float, string?>> updateMethods,
            Dictionary<string, Action<IAIContext>?> enterMethods,
            Dictionary<string, Action<IAIContext>?> exitMethods,
            Dictionary<string, float> delays)
        {
            _initialState = initialState;
            _updateMethods = updateMethods;
            _enterMethods = enterMethods;
            _exitMethods = exitMethods;
            _delays = delays;
        }

        public AIStateMachine Build()
        {
            return new AIStateMachine(
                _initialState,
                new Dictionary<string, Func<IAIContext, float, string?>>(_updateMethods),
                new Dictionary<string, Action<IAIContext>?>(_enterMethods),
                new Dictionary<string, Action<IAIContext>?>(_exitMethods),
                new Dictionary<string, float>(_delays));
        }
    }
}
