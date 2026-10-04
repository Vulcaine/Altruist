using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Altruist;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Combat;

public interface ICombatEventDispatcher
{
    void Dispatch<TEvent>(TEvent payload, object primary) where TEvent : ICombatEventPayload;
    void Dispatch<TEvent>(TEvent payload, object primary, object secondary) where TEvent : ICombatEventPayload;
}

[Service(typeof(ICombatEventDispatcher))]
public sealed class CombatEventDispatcher : ICombatEventDispatcher
{
    private readonly ILogger _logger;

    public CombatEventDispatcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CombatEventDispatcher>();
    }

    public void Dispatch<TEvent>(TEvent payload, object primary) where TEvent : ICombatEventPayload
    {
        DispatchInternal(payload!, primary, null);
    }

    public void Dispatch<TEvent>(TEvent payload, object primary, object secondary) where TEvent : ICombatEventPayload
    {
        DispatchInternal(payload!, primary, secondary);
    }

    private void DispatchInternal(object payload, object primary, object? secondary)
    {
        foreach (var handler in CombatEventHandlerRegistry.GetHandlers(payload.GetType(), primary.GetType()))
            Invoke(handler, payload, primary, null);

        if (secondary == null)
            return;

        foreach (var handler in CombatEventHandlerRegistry.GetHandlers(payload.GetType(), primary.GetType(), secondary.GetType()))
        {
            if (handler.ParamTypeB == null)
                continue;

            if (handler.ParamTypeA.IsInstanceOfType(primary) && handler.ParamTypeB.IsInstanceOfType(secondary))
                Invoke(handler, payload, primary, secondary);
            else if (handler.ParamTypeA.IsInstanceOfType(secondary) && handler.ParamTypeB.IsInstanceOfType(primary))
                Invoke(handler, payload, secondary, primary);
        }
    }

    private void Invoke(CombatEventHandlerRegistry.HandlerDescriptor handler, object payload, object primary, object? secondary)
    {
        try
        {
            ((Action<object, object, object?>)handler.Invoker)(payload, primary, secondary);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Combat event handler {Handler} failed for {Event}({A}, {B})",
                handler.HandlerType.Name,
                payload.GetType().Name,
                primary.GetType().Name,
                secondary?.GetType().Name ?? "none");
        }
    }
}

public static class CombatEventHandlerDiscovery
{
    public static void RegisterCombatHandlers(
        IEnumerable<Assembly> assemblies,
        Func<Type, object?> instanceFactory,
        ILogger logger)
    {
        if (assemblies is null)
            throw new ArgumentNullException(nameof(assemblies));
        if (instanceFactory is null)
            throw new ArgumentNullException(nameof(instanceFactory));
        if (logger is null)
            throw new ArgumentNullException(nameof(logger));

        var handlerTypes = TypeDiscovery.FindTypesWithAttribute<CombatHandlerAttribute>(assemblies);

        foreach (var handlerType in handlerTypes)
        {
            object? instance = instanceFactory(handlerType) ?? Activator.CreateInstance(handlerType);
            if (instance == null)
            {
                logger.LogWarning("Could not create instance of combat handler type {Type}. Skipping.", handlerType.FullName);
                continue;
            }

            RegisterCombatMethodsFromInstance(instance, logger);
        }
    }

    private static void RegisterCombatMethodsFromInstance(object instance, ILogger log)
    {
        var type = instance.GetType();
        var methodsWithAttr = TypeDiscovery.FindInstanceMethodsWithAttribute<CombatEventAttribute>(type);

        foreach (var (method, attr) in methodsWithAttr)
        {
            var pars = method.GetParameters();
            if (pars.Length is not (2 or 3))
                throw new InvalidOperationException(
                    $"Method {type.Name}.{method.Name} marked with [CombatEvent] must have payload first, then one or two actor parameters.");

            var payloadType = pars[0].ParameterType;
            var paramA = pars[1].ParameterType;
            Type? paramB = pars.Length == 3 ? pars[2].ParameterType : null;

            if (!typeof(ICombatEventPayload).IsAssignableFrom(payloadType))
                throw new InvalidOperationException(
                    $"First parameter of {type.Name}.{method.Name} must implement {nameof(ICombatEventPayload)}.");

            if (!payloadType.IsAssignableFrom(attr.EventType) && !attr.EventType.IsAssignableFrom(payloadType))
                throw new InvalidOperationException(
                    $"First parameter of {type.Name}.{method.Name} must be compatible with the [CombatEvent] event type.");

            if (!paramA.IsClass || paramA.IsAbstract)
                throw new InvalidOperationException(
                    $"Second parameter of {type.Name}.{method.Name} must be a concrete reference type.");

            if (paramB != null && (!paramB.IsClass || paramB.IsAbstract))
                throw new InvalidOperationException(
                    $"Third parameter of {type.Name}.{method.Name} must be a concrete reference type.");

            if (method.ReturnType != typeof(void))
                throw new InvalidOperationException(
                    $"Method {type.Name}.{method.Name} marked with [CombatEvent] must return void.");

            var invoker = BuildInvoker(instance, method, payloadType, paramA, paramB);

            CombatEventHandlerRegistry.Register(new CombatEventHandlerRegistry.HandlerDescriptor(
                HandlerType: type,
                EventType: attr.EventType,
                ParamTypeA: paramA,
                ParamTypeB: paramB,
                Invoker: invoker));

            log.LogDebug(
                "Registered combat handler {Handler}.{Method} for {Event}({ParamA}, {ParamB}).",
                type.FullName,
                method.Name,
                attr.EventType.FullName,
                paramA.FullName,
                paramB?.FullName ?? "none");
        }
    }

    private static Delegate BuildInvoker(
        object target,
        MethodInfo method,
        Type payloadType,
        Type paramA,
        Type? paramB)
    {
        var targetConst = Expression.Constant(target);
        var payload = Expression.Parameter(typeof(object), "payload");
        var argA = Expression.Parameter(typeof(object), "a");
        var argB = Expression.Parameter(typeof(object), "b");

        var castPayload = Expression.Convert(payload, payloadType);
        var castA = Expression.Convert(argA, paramA);

        var call = paramB == null
            ? Expression.Call(targetConst, method, castPayload, castA)
            : Expression.Call(targetConst, method, castPayload, castA, Expression.Convert(argB, paramB));

        var lambda = Expression.Lambda<Action<object, object, object?>>(call, payload, argA, argB);
        return lambda.Compile();
    }
}

public static class CombatEventHandlerRegistry
{
    public sealed record HandlerDescriptor(
        Type HandlerType,
        Type EventType,
        Type ParamTypeA,
        Type? ParamTypeB,
        Delegate Invoker);

    private sealed record HandlerKey(Type EventType, Type A, Type? B);

    private sealed class HandlerKeyComparer : IEqualityComparer<HandlerKey>
    {
        public bool Equals(HandlerKey? x, HandlerKey? y)
            => x is not null && y is not null && x.EventType == y.EventType && x.A == y.A && x.B == y.B;

        public int GetHashCode(HandlerKey obj)
            => HashCode.Combine(obj.EventType, obj.A, obj.B);
    }

    private static readonly ConcurrentDictionary<HandlerKey, List<HandlerDescriptor>> _handlers =
        new(new HandlerKeyComparer());

    private static readonly ConcurrentDictionary<(Type, Type, Type?), HandlerKey> _keyCache = new();

    public static void Register(HandlerDescriptor descriptor, bool alsoRegisterSymmetric = true)
    {
        if (descriptor is null)
            throw new ArgumentNullException(nameof(descriptor));

        var key = GetOrCreateKey(descriptor.EventType, descriptor.ParamTypeA, descriptor.ParamTypeB);
        Add(key, descriptor);

        if (alsoRegisterSymmetric && descriptor.ParamTypeB != null && descriptor.ParamTypeA != descriptor.ParamTypeB)
        {
            var symmetricKey = GetOrCreateKey(descriptor.EventType, descriptor.ParamTypeB, descriptor.ParamTypeA);
            Add(symmetricKey, descriptor);
        }
    }

    public static IReadOnlyList<HandlerDescriptor> GetHandlers(Type eventType, Type aType)
    {
        var key = GetOrCreateKey(eventType, aType, null);
        return _handlers.TryGetValue(key, out var list)
            ? list
            : Array.Empty<HandlerDescriptor>();
    }

    public static IReadOnlyList<HandlerDescriptor> GetHandlers(Type eventType, Type aType, Type bType)
    {
        var key = GetOrCreateKey(eventType, aType, bType);
        return _handlers.TryGetValue(key, out var list)
            ? list
            : Array.Empty<HandlerDescriptor>();
    }

    public static int TotalHandlerCount => _handlers.Values.Sum(l => l.Count);

    public static void ClearForTests()
    {
        _handlers.Clear();
        _keyCache.Clear();
    }

    private static void Add(HandlerKey key, HandlerDescriptor descriptor)
    {
        var list = _handlers.GetOrAdd(key, _ => new List<HandlerDescriptor>());
        lock (list)
        {
            list.Add(descriptor);
        }
    }

    private static HandlerKey GetOrCreateKey(Type eventType, Type a, Type? b)
        => _keyCache.GetOrAdd((eventType, a, b), static k => new HandlerKey(k.Item1, k.Item2, k.Item3));
}
