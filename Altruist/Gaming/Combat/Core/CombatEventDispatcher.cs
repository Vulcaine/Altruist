using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using Altruist;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Combat;

/// <summary>
/// Routes a combat payload to every matching <see cref="CombatEventAttribute"/> method on
/// <see cref="CombatHandlerAttribute"/> classes. <see cref="CombatService"/> calls it for every hit, sweep and death;
/// call it yourself to raise custom <see cref="ICombatEventPayload"/> events through the same handler classes.
/// </summary>
/// <remarks>
/// Registered as a singleton (<c>[Service(typeof(ICombatEventDispatcher))]</c>). Dispatch is synchronous on the
/// calling thread; handler exceptions are logged, not rethrown. Handler lookup uses exact runtime types
/// (see <see cref="CombatEventAttribute"/>). For subscribe-to-everything callbacks use the events on
/// <see cref="ICombatService"/> instead.
/// </remarks>
public interface ICombatEventDispatcher
{
    /// <summary>
    /// Invokes single-actor handlers registered for (<c>payload.GetType()</c>, <c>primary.GetType()</c>).
    /// </summary>
    /// <typeparam name="TEvent">Payload type.</typeparam>
    /// <param name="payload">Event data passed as the handler's first argument.</param>
    /// <param name="primary">The actor the event is about (e.g. the attacker or the victim).</param>
    void Dispatch<TEvent>(TEvent payload, object primary) where TEvent : ICombatEventPayload;
    /// <summary>
    /// Invokes single-actor handlers for <paramref name="primary"/>, then two-actor handlers registered for the
    /// (<paramref name="primary"/>, <paramref name="secondary"/>) runtime-type pair in either order, swapping the
    /// arguments to fit each handler's declared parameter order. Single-actor handlers for
    /// <paramref name="secondary"/> are NOT invoked.
    /// </summary>
    /// <typeparam name="TEvent">Payload type.</typeparam>
    /// <param name="payload">Event data passed as the handler's first argument.</param>
    /// <param name="primary">First actor (e.g. attacker, or victim for deaths).</param>
    /// <param name="secondary">Second actor (e.g. target, or killer for deaths).</param>
    void Dispatch<TEvent>(TEvent payload, object primary, object secondary) where TEvent : ICombatEventPayload;
}

/// <summary>
/// Default <see cref="ICombatEventDispatcher"/>: reads handlers from the static <see cref="CombatEventHandlerRegistry"/>.
/// Resolve <see cref="ICombatEventDispatcher"/> from DI rather than constructing this directly.
/// </summary>
[Service(typeof(ICombatEventDispatcher))]
public sealed class CombatEventDispatcher : ICombatEventDispatcher
{
    private readonly ILogger _logger;

    /// <summary>Creates the dispatcher (called by DI).</summary>
    /// <param name="loggerFactory">Used to log handler failures.</param>
    public CombatEventDispatcher(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<CombatEventDispatcher>();
    }

    /// <inheritdoc/>
    public void Dispatch<TEvent>(TEvent payload, object primary) where TEvent : ICombatEventPayload
    {
        DispatchInternal(payload!, primary, null);
    }

    /// <inheritdoc/>
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

/// <summary>
/// Scans assemblies for <see cref="CombatHandlerAttribute"/> classes and registers their
/// <see cref="CombatEventAttribute"/> methods in <see cref="CombatEventHandlerRegistry"/> as compiled delegates.
/// </summary>
/// <remarks>
/// Normally invoked once by <see cref="CombatHandlerInitializer"/> at startup; you only call it directly in tests or
/// custom hosts that bypass the Altruist bootstrap. Calling it twice registers every handler twice (the registry is
/// static and does not de-duplicate); use <see cref="CombatEventHandlerRegistry.ClearForTests"/> between test runs.
/// </remarks>
public static class CombatEventHandlerDiscovery
{
    /// <summary>
    /// Instantiates every <c>[CombatHandler]</c> type found in <paramref name="assemblies"/> and registers its
    /// <c>[CombatEvent]</c> methods.
    /// </summary>
    /// <param name="assemblies">Assemblies to scan.</param>
    /// <param name="instanceFactory">Returns the handler instance for a type (normally the DI container's
    /// <c>GetService</c>); when it returns null the type's parameterless constructor is used, and the type is
    /// skipped with a warning if that also yields null.</param>
    /// <param name="logger">Receives skip warnings and per-method debug registrations.</param>
    /// <exception cref="ArgumentNullException">Any argument is null.</exception>
    /// <exception cref="InvalidOperationException">A <c>[CombatEvent]</c> method has an invalid signature
    /// (see <see cref="CombatEventAttribute"/>).</exception>
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

/// <summary>
/// Process-wide static table of combat event handlers, keyed by (event type, actor A type, actor B type).
/// Filled by <see cref="CombatEventHandlerDiscovery"/> and read by <see cref="CombatEventDispatcher"/>.
/// </summary>
/// <remarks>
/// Thread-safe for concurrent registration and lookup. Being static, it is shared by every host in the process;
/// tests that boot several hosts should call <see cref="ClearForTests"/> to avoid duplicate handlers.
/// Prefer declaring handlers with <see cref="CombatHandlerAttribute"/>; call <see cref="Register"/> directly only
/// for handlers built at runtime.
/// </remarks>
public static class CombatEventHandlerRegistry
{
    /// <summary>One registered handler method.</summary>
    /// <param name="HandlerType">Declaring <c>[CombatHandler]</c> class.</param>
    /// <param name="EventType">Payload type from <see cref="CombatEventAttribute.EventType"/>.</param>
    /// <param name="ParamTypeA">Declared type of the first actor parameter.</param>
    /// <param name="ParamTypeB">Declared type of the second actor parameter, or null for single-actor handlers.</param>
    /// <param name="Invoker">Compiled <c>Action&lt;object, object, object?&gt;</c> (payload, a, b) that calls the method.</param>
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

    /// <summary>Adds a handler under its (event, A, B) key.</summary>
    /// <param name="descriptor">Handler to add; <see cref="HandlerDescriptor.Invoker"/> must be an
    /// <c>Action&lt;object, object, object?&gt;</c>.</param>
    /// <param name="alsoRegisterSymmetric">When true and the handler has two different actor types, also registers it
    /// under (event, B, A) so it fires regardless of which actor the caller passes first.</param>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is null.</exception>
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

    /// <summary>Returns single-actor handlers registered for exactly (<paramref name="eventType"/>, <paramref name="aType"/>).</summary>
    /// <param name="eventType">Exact payload runtime type.</param>
    /// <param name="aType">Exact actor runtime type.</param>
    /// <returns>The live handler list (do not mutate), or an empty list.</returns>
    public static IReadOnlyList<HandlerDescriptor> GetHandlers(Type eventType, Type aType)
    {
        var key = GetOrCreateKey(eventType, aType, null);
        return _handlers.TryGetValue(key, out var list)
            ? list
            : Array.Empty<HandlerDescriptor>();
    }

    /// <summary>Returns two-actor handlers registered for exactly (<paramref name="eventType"/>, <paramref name="aType"/>, <paramref name="bType"/>).</summary>
    /// <param name="eventType">Exact payload runtime type.</param>
    /// <param name="aType">Exact first actor runtime type.</param>
    /// <param name="bType">Exact second actor runtime type.</param>
    /// <returns>The live handler list (do not mutate), or an empty list.</returns>
    public static IReadOnlyList<HandlerDescriptor> GetHandlers(Type eventType, Type aType, Type bType)
    {
        var key = GetOrCreateKey(eventType, aType, bType);
        return _handlers.TryGetValue(key, out var list)
            ? list
            : Array.Empty<HandlerDescriptor>();
    }

    /// <summary>Total number of registrations across all keys (symmetric registrations count twice).</summary>
    public static int TotalHandlerCount => _handlers.Values.Sum(l => l.Count);

    /// <summary>Removes every registration. Test-only: call between test hosts so handlers are not registered twice.</summary>
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
