using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Collections.ObjectModel;

namespace Altruist.Physx
{
    /// <summary>
    /// Process-wide (static) registry of collision handlers. Each handler declares the two parameter types it accepts,
    /// <c>(ParamTypeA, ParamTypeB)</c>, and stores a compiled invoker delegate of type
    /// <c>Action&lt;object?, object, object&gt;</c> taking <c>(payload, a, b)</c>.
    /// </summary>
    /// <remarks>
    /// Populated automatically at startup by <see cref="AltruistCollisionHandlerConfig"/> /
    /// <see cref="CollisionHandlerDiscovery"/>; dispatchers (e.g. the gaming package's spatial collision
    /// dispatcher) read it. A handler matches a pair of runtime types when each runtime type is assignable to the
    /// corresponding parameter type, so a handler declared for a base class or interface also fires for derived types.
    /// Results are computed once per (runtime type pair, event) and cached, so lookups do not allocate after the first
    /// call. Returned lists are immutable snapshots; registering or clearing is thread-safe and invalidates the cache,
    /// and lists already handed out keep their old contents.
    /// </remarks>
    public static class CollisionHandlerRegistry
    {
        /// <summary>One registered handler method.</summary>
        /// <param name="HandlerType">Class that declares the handler method.</param>
        /// <param name="ParamTypeA">Declared type of the method's second parameter (first entity).</param>
        /// <param name="ParamTypeB">Declared type of the method's third parameter (second entity).</param>
        /// <param name="EventType">Event type from <see cref="CollisionEventAttribute.EventType"/>.</param>
        /// <param name="Invoker">
        /// Compiled <c>Action&lt;object?, object, object&gt;</c> taking <c>(payload, a, b)</c>; <c>a</c> must be an
        /// instance of <see cref="ParamTypeA"/> and <c>b</c> of <see cref="ParamTypeB"/> (the caller must swap arguments
        /// when the descriptor matched the pair in swapped order).
        /// </param>
        public sealed record HandlerDescriptor(
            Type HandlerType,
            Type ParamTypeA,
            Type ParamTypeB,
            Type EventType,
            Delegate Invoker);

        private sealed record Registration(HandlerDescriptor Descriptor, bool Symmetric);

        private sealed class State
        {
            public ImmutableArray<Registration> Registrations { get; }

            public ConcurrentDictionary<(Type A, Type B, Type? Event), IReadOnlyList<HandlerDescriptor>> Lookups { get; } = new();

            public State(ImmutableArray<Registration> registrations) => Registrations = registrations;
        }

        private static readonly IReadOnlyList<HandlerDescriptor> None = Array.Empty<HandlerDescriptor>();

        private static readonly object _gate = new();

        private static volatile State _state = new(ImmutableArray<Registration>.Empty);

        /// <summary>
        /// Adds a handler. It matches pairs whose first object is a <c>ParamTypeA</c> and second a <c>ParamTypeB</c>
        /// and, by default, also the swapped order, so the handler is found regardless of contact order.
        /// </summary>
        /// <param name="descriptor">Handler to add.</param>
        /// <param name="alsoRegisterSymmetric">
        /// When <see langword="true"/> (default), the handler also matches <c>(ParamTypeB, ParamTypeA)</c> pairs. It is
        /// still stored once, so dispatchers must check argument order before invoking.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
        public static void Register(HandlerDescriptor descriptor, bool alsoRegisterSymmetric = true)
        {
            if (descriptor is null)
                throw new ArgumentNullException(nameof(descriptor));

            lock (_gate)
            {
                _state = new State(_state.Registrations.Add(new Registration(descriptor, alsoRegisterSymmetric)));
            }
        }

        /// <summary>
        /// Returns every handler matching the runtime type pair, for any event type, in registration order. Use
        /// <see cref="GetHandlers(Type, Type, Type)"/> to filter by event.
        /// </summary>
        /// <param name="aType">Runtime type of the first object.</param>
        /// <param name="bType">Runtime type of the second object.</param>
        /// <returns>An immutable list, empty when nothing matches.</returns>
        public static IReadOnlyList<HandlerDescriptor> GetHandlers(Type aType, Type bType)
            => Lookup(aType, bType, null);

        /// <summary>
        /// Returns the handlers matching the runtime type pair whose <see cref="HandlerDescriptor.EventType"/> equals
        /// <paramref name="eventType"/> exactly, in registration order.
        /// </summary>
        /// <param name="aType">Runtime type of the first object.</param>
        /// <param name="bType">Runtime type of the second object.</param>
        /// <param name="eventType">Event type to filter on (e.g. <c>typeof(CollisionEnter)</c>).</param>
        /// <returns>An immutable list, empty when nothing matches.</returns>
        public static IReadOnlyList<HandlerDescriptor> GetHandlers(Type aType, Type bType, Type eventType)
        {
            if (eventType is null)
                throw new ArgumentNullException(nameof(eventType));

            return Lookup(aType, bType, eventType);
        }

        /// <summary>
        /// Cheap pre-check: whether any handler (for any event type) matches the runtime type pair. Use it to skip
        /// overlap tests for pairs nobody listens to.
        /// </summary>
        /// <param name="aType">Runtime type of the first object.</param>
        /// <param name="bType">Runtime type of the second object.</param>
        public static bool HasHandlers(Type aType, Type bType) => Lookup(aType, bType, null).Count > 0;

        /// <summary>Number of registered handlers; a symmetric registration counts once.</summary>
        public static int TotalHandlerCount => _state.Registrations.Length;

        /// <summary>Removes all handlers and cached lookups. Called by the startup bootstrap before re-registering.</summary>
        public static void Clear()
        {
            lock (_gate)
            {
                _state = new State(ImmutableArray<Registration>.Empty);
            }
        }

        private static IReadOnlyList<HandlerDescriptor> Lookup(Type aType, Type bType, Type? eventType)
        {
            if (aType is null)
                throw new ArgumentNullException(nameof(aType));
            if (bType is null)
                throw new ArgumentNullException(nameof(bType));

            var state = _state;
            return state.Lookups.GetOrAdd((aType, bType, eventType), key => Resolve(state.Registrations, key));
        }

        private static IReadOnlyList<HandlerDescriptor> Resolve(
            ImmutableArray<Registration> registrations,
            (Type A, Type B, Type? Event) key)
        {
            var matches = registrations
                .Where(r => key.Event is null || r.Descriptor.EventType == key.Event)
                .Where(r => Matches(r, key.A, key.B))
                .Select(r => r.Descriptor)
                .ToArray();

            return matches.Length == 0 ? None : new ReadOnlyCollection<HandlerDescriptor>(matches);
        }

        private static bool Matches(Registration registration, Type aType, Type bType)
        {
            var d = registration.Descriptor;
            if (d.ParamTypeA.IsAssignableFrom(aType) && d.ParamTypeB.IsAssignableFrom(bType))
                return true;

            return registration.Symmetric
                && d.ParamTypeA.IsAssignableFrom(bType)
                && d.ParamTypeB.IsAssignableFrom(aType);
        }
    }
}
