using System.Collections.Concurrent;
using System.Collections.Immutable;
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
/// calling thread; handler exceptions are logged, not rethrown. A handler runs for the exact event type it declares
/// and for actors of its parameter types or any subtype (see <see cref="CombatEventAttribute"/>). For subscribe-to-everything callbacks use the events on
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
                Invoker: invoker,
                Method: method));

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
/// Process-wide static table of combat event handlers.
/// Filled by <see cref="CombatEventHandlerDiscovery"/> and read by <see cref="CombatEventDispatcher"/>.
/// </summary>
/// <remarks>
/// Thread-safe: registration replaces an immutable snapshot, so lookups never see a list being changed. Being static,
/// it is shared by every host in the process; registering the same handler method again (a second discovery, another
/// host) replaces the earlier registration instead of adding a duplicate. Prefer declaring handlers with
/// <see cref="CombatHandlerAttribute"/>; call <see cref="Register"/> directly only for handlers built at runtime.
/// </remarks>
public static class CombatEventHandlerRegistry
{
    /// <summary>One registered handler method.</summary>
    /// <param name="HandlerType">Declaring <c>[CombatHandler]</c> class.</param>
    /// <param name="EventType">Payload type from <see cref="CombatEventAttribute.EventType"/>.</param>
    /// <param name="ParamTypeA">Declared type of the first actor parameter.</param>
    /// <param name="ParamTypeB">Declared type of the second actor parameter, or null for single-actor handlers.</param>
    /// <param name="Invoker">Compiled <c>Action&lt;object, object, object?&gt;</c> (payload, a, b) that calls the method.</param>
    /// <param name="Method">The handler method; a later registration of the same method replaces this one. Null for a
    /// handler without a method identity (never replaced).</param>
    public sealed record HandlerDescriptor(
        Type HandlerType,
        Type EventType,
        Type ParamTypeA,
        Type? ParamTypeB,
        Delegate Invoker,
        MethodInfo? Method = null);

    private sealed record Entry(HandlerDescriptor Descriptor, bool Symmetric);

    // The handlers and the lookups resolved from exactly those handlers; replaced as a whole on every change.
    private sealed class Table(ImmutableArray<Entry> entries)
    {
        public ImmutableArray<Entry> Entries { get; } = entries;
        public ConcurrentDictionary<(Type Event, Type A, Type? B), HandlerDescriptor[]> Resolved { get; } = new();
    }

    private static readonly object _gate = new();
    private static volatile Table _table = new(ImmutableArray<Entry>.Empty);

    /// <summary>Adds a handler, replacing an earlier registration of the same <see cref="HandlerDescriptor.Method"/>.</summary>
    /// <param name="descriptor">Handler to add; <see cref="HandlerDescriptor.Invoker"/> must be an
    /// <c>Action&lt;object, object, object?&gt;</c>.</param>
    /// <param name="alsoRegisterSymmetric">When true and the handler has two actors, it also fires when the caller
    /// passes the actors in the other order.</param>
    /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is null.</exception>
    public static void Register(HandlerDescriptor descriptor, bool alsoRegisterSymmetric = true)
    {
        if (descriptor is null)
            throw new ArgumentNullException(nameof(descriptor));

        lock (_gate)
        {
            var entries = _table.Entries;
            var kept = descriptor.Method is null
                ? entries
                : entries.RemoveAll(e => e.Descriptor.Method == descriptor.Method && e.Descriptor.HandlerType == descriptor.HandlerType);
            _table = new Table(kept.Add(new Entry(descriptor, alsoRegisterSymmetric && descriptor.ParamTypeB != null)));
        }
    }

    /// <summary>Single-actor handlers for <paramref name="eventType"/> whose actor parameter accepts <paramref name="aType"/>.</summary>
    /// <param name="eventType">Payload runtime type (matched exactly).</param>
    /// <param name="aType">Actor runtime type (the handler's parameter type or a base of it).</param>
    /// <returns>The handlers in registration order (an immutable snapshot), or an empty list.</returns>
    public static IReadOnlyList<HandlerDescriptor> GetHandlers(Type eventType, Type aType)
        => Resolve(eventType, aType, null);

    /// <summary>Two-actor handlers for <paramref name="eventType"/> that accept the actors in this order, or (when
    /// registered symmetric) in the other order.</summary>
    /// <param name="eventType">Payload runtime type (matched exactly).</param>
    /// <param name="aType">First actor runtime type.</param>
    /// <param name="bType">Second actor runtime type.</param>
    /// <returns>The handlers in registration order (an immutable snapshot), or an empty list.</returns>
    public static IReadOnlyList<HandlerDescriptor> GetHandlers(Type eventType, Type aType, Type bType)
        => Resolve(eventType, aType, bType);

    /// <summary>Number of registered handlers.</summary>
    public static int TotalHandlerCount => _table.Entries.Length;

    /// <summary>Removes every handler. For tests that need an empty table.</summary>
    public static void ClearForTests()
    {
        lock (_gate)
            _table = new Table(ImmutableArray<Entry>.Empty);
    }

    private static HandlerDescriptor[] Resolve(Type eventType, Type aType, Type? bType)
    {
        var table = _table;
        return table.Resolved.GetOrAdd((eventType, aType, bType), key => table.Entries
            .Where(e => Matches(e, key.Event, key.A, key.B))
            .Select(e => e.Descriptor)
            .ToArray());
    }

    private static bool Matches(Entry e, Type eventType, Type aType, Type? bType)
    {
        var d = e.Descriptor;
        if (d.EventType != eventType) return false;
        if (bType is null)
            return d.ParamTypeB is null && d.ParamTypeA.IsAssignableFrom(aType);
        if (d.ParamTypeB is null) return false;
        return (d.ParamTypeA.IsAssignableFrom(aType) && d.ParamTypeB.IsAssignableFrom(bType))
            || (e.Symmetric && d.ParamTypeA.IsAssignableFrom(bType) && d.ParamTypeB.IsAssignableFrom(aType));
    }
}
