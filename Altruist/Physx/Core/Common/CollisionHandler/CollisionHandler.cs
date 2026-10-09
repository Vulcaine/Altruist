using System.Collections.Concurrent;

namespace Altruist.Physx
{
    /// <summary>
    /// Process-wide (static) registry of collision handlers keyed by the exact runtime types of the two
    /// colliding objects, <c>(TypeA, TypeB)</c>. Each entry stores a compiled invoker delegate of type
    /// <c>Action&lt;object?, object, object&gt;</c> taking <c>(payload, a, b)</c>.
    /// </summary>
    /// <remarks>
    /// Populated automatically at startup by <see cref="AltruistCollisionHandlerConfig"/> /
    /// <see cref="CollisionHandlerDiscovery"/>; dispatchers (e.g. the gaming package's spatial collision
    /// dispatcher) read it. Lookups are by exact type: a handler declared for a base class is not found for a
    /// derived runtime type. Lookups do not allocate after the first call per type pair (cached keys, and
    /// per-event filtered lists cached on first request). Registration is intended for startup; returned lists are
    /// the live internal lists, so do not register concurrently with dispatch.
    /// </remarks>
    public static class CollisionHandlerRegistry
    {
        /// <summary>Ordered type-pair key used for lookups.</summary>
        /// <param name="A">Runtime type of the first object.</param>
        /// <param name="B">Runtime type of the second object.</param>
        public sealed record HandlerKey(Type A, Type B);

        /// <summary>One registered handler method.</summary>
        /// <param name="HandlerType">Class that declares the handler method.</param>
        /// <param name="ParamTypeA">Declared type of the method's second parameter (first entity).</param>
        /// <param name="ParamTypeB">Declared type of the method's third parameter (second entity).</param>
        /// <param name="EventType">Event type from <see cref="CollisionEventAttribute.EventType"/>.</param>
        /// <param name="Invoker">
        /// Compiled <c>Action&lt;object?, object, object&gt;</c> taking <c>(payload, a, b)</c>; <c>a</c> must be an
        /// instance of <see cref="ParamTypeA"/> and <c>b</c> of <see cref="ParamTypeB"/> (the caller must swap arguments
        /// when the descriptor was found via the symmetric key).
        /// </param>
        public sealed record HandlerDescriptor(
            Type HandlerType,
            Type ParamTypeA,
            Type ParamTypeB,
            Type EventType,
            Delegate Invoker);

        private sealed class HandlerKeyComparer : IEqualityComparer<HandlerKey>
        {
            public bool Equals(HandlerKey? x, HandlerKey? y)
                => x is not null && y is not null && x.A == y.A && x.B == y.B;

            public int GetHashCode(HandlerKey obj)
                => HashCode.Combine(obj.A, obj.B);
        }

        private static readonly ConcurrentDictionary<HandlerKey, List<HandlerDescriptor>> _handlers =
            new(new HandlerKeyComparer());

        // Pre-grouped by event type — avoids .Where().ToList() per dispatch
        private static readonly ConcurrentDictionary<(HandlerKey, Type), List<HandlerDescriptor>> _byEvent = new();

        // Cached key lookups — avoids new HandlerKey() per HasHandlers/GetHandlers call
        private static readonly ConcurrentDictionary<(Type, Type), HandlerKey> _keyCache = new();

        /// <summary>
        /// Adds a handler under <c>(ParamTypeA, ParamTypeB)</c> and, by default, also under the swapped key so the
        /// handler is found regardless of contact order. Invalidates the cached per-event lists for the affected keys.
        /// </summary>
        /// <param name="descriptor">Handler to add.</param>
        /// <param name="alsoRegisterSymmetric">
        /// When <see langword="true"/> (default) and the two types differ, also register under <c>(ParamTypeB, ParamTypeA)</c>.
        /// The same descriptor is stored, so dispatchers must check argument order before invoking.
        /// </param>
        /// <exception cref="ArgumentNullException"><paramref name="descriptor"/> is <see langword="null"/>.</exception>
        public static void Register(HandlerDescriptor descriptor, bool alsoRegisterSymmetric = true)
        {
            if (descriptor is null)
                throw new ArgumentNullException(nameof(descriptor));

            var key = GetOrCreateKey(descriptor.ParamTypeA, descriptor.ParamTypeB);
            var list = _handlers.GetOrAdd(key, _ => new List<HandlerDescriptor>());
            lock (list)
            {
                list.Add(descriptor);
            }
            InvalidateEventCache(key);

            if (alsoRegisterSymmetric && descriptor.ParamTypeA != descriptor.ParamTypeB)
            {
                var symmetricKey = GetOrCreateKey(descriptor.ParamTypeB, descriptor.ParamTypeA);
                var symmetricList = _handlers.GetOrAdd(symmetricKey, _ => new List<HandlerDescriptor>());
                lock (symmetricList)
                {
                    symmetricList.Add(descriptor);
                }
                InvalidateEventCache(symmetricKey);
            }
        }

        /// <summary>
        /// Returns all registered handlers for the exact type pair, for any event type. Use
        /// <see cref="GetHandlers(Type, Type, Type)"/> to filter by event.
        /// </summary>
        /// <param name="aType">Runtime type of the first object.</param>
        /// <param name="bType">Runtime type of the second object.</param>
        /// <returns>The live internal list (do not mutate), or an empty list when none are registered.</returns>
        public static IReadOnlyList<HandlerDescriptor> GetHandlers(Type aType, Type bType)
        {
            var key = GetOrCreateKey(aType, bType);
            return _handlers.TryGetValue(key, out var list)
                ? list
                : Array.Empty<HandlerDescriptor>();
        }

        /// <summary>
        /// Returns handlers for the exact type pair whose <see cref="HandlerDescriptor.EventType"/> equals
        /// <paramref name="eventType"/> exactly. The filtered list is built once per (pair, event) and cached; later calls do not allocate.
        /// </summary>
        /// <param name="aType">Runtime type of the first object.</param>
        /// <param name="bType">Runtime type of the second object.</param>
        /// <param name="eventType">Event type to filter on (e.g. <c>typeof(CollisionEnter)</c>).</param>
        /// <returns>Matching handlers (cached list, do not mutate), or an empty list.</returns>
        public static IReadOnlyList<HandlerDescriptor> GetHandlers(Type aType, Type bType, Type eventType)
        {
            var key = GetOrCreateKey(aType, bType);
            var cacheKey = (key, eventType);

            if (_byEvent.TryGetValue(cacheKey, out var cached))
                return cached;

            // Build and cache the filtered list (one-time cost per type-pair-event combo)
            if (!_handlers.TryGetValue(key, out var all) || all.Count == 0)
                return Array.Empty<HandlerDescriptor>();

            var filtered = new List<HandlerDescriptor>();
            for (int i = 0; i < all.Count; i++)
            {
                if (all[i].EventType == eventType)
                    filtered.Add(all[i]);
            }

            _byEvent[cacheKey] = filtered;
            return filtered;
        }

        /// <summary>
        /// Cheap pre-check: whether any handler (for any event type) exists for the exact type pair. Use it to skip
        /// overlap tests for pairs nobody listens to.
        /// </summary>
        /// <param name="aType">Runtime type of the first object.</param>
        /// <param name="bType">Runtime type of the second object.</param>
        public static bool HasHandlers(Type aType, Type bType)
        {
            var key = GetOrCreateKey(aType, bType);
            return _handlers.TryGetValue(key, out var list) && list.Count > 0;
        }

        /// <summary>
        /// Number of stored handler entries across all keys. Symmetric registrations are counted twice (once per key).
        /// Enumerates the registry; not intended for hot paths.
        /// </summary>
        public static int TotalHandlerCount => _handlers.Values.Sum(l => l.Count);

        /// <summary>Removes all handlers and caches. Called by the startup bootstrap before re-registering.</summary>
        public static void Clear()
        {
            _handlers.Clear();
            _byEvent.Clear();
            _keyCache.Clear();
        }

        private static HandlerKey GetOrCreateKey(Type a, Type b)
            => _keyCache.GetOrAdd((a, b), static k => new HandlerKey(k.Item1, k.Item2));

        private static void InvalidateEventCache(HandlerKey key)
        {
            // Remove all cached event-filtered lists for this key
            foreach (var cacheKey in _byEvent.Keys)
            {
                if (cacheKey.Item1 == key)
                    _byEvent.TryRemove(cacheKey, out _);
            }
        }
    }
}
