using System.Collections.Concurrent;

namespace Altruist.Physx
{
    /// <summary>
    /// Registry of collision handlers keyed by (TypeA, TypeB).
    /// Internally stores compiled delegates that take (object a, object b)
    /// and do the appropriate typed call.
    /// Zero-allocation lookups via cached keys and pre-grouped event types.
    /// </summary>
    public static class CollisionHandlerRegistry
    {
        public sealed record HandlerKey(Type A, Type B);

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
        /// Returns ALL registered handlers for the type pair (any event type). Zero allocation.
        /// </summary>
        public static IReadOnlyList<HandlerDescriptor> GetHandlers(Type aType, Type bType)
        {
            var key = GetOrCreateKey(aType, bType);
            return _handlers.TryGetValue(key, out var list)
                ? list
                : Array.Empty<HandlerDescriptor>();
        }

        /// <summary>
        /// Returns handlers filtered by event type. Zero allocation (pre-grouped cache).
        /// </summary>
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

        /// <summary>Check if any handlers exist for this type pair. Zero allocation.</summary>
        public static bool HasHandlers(Type aType, Type bType)
        {
            var key = GetOrCreateKey(aType, bType);
            return _handlers.TryGetValue(key, out var list) && list.Count > 0;
        }

        public static int TotalHandlerCount => _handlers.Values.Sum(l => l.Count);

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
