using System.Collections.Concurrent;

namespace Altruist.Physx
{
    /// <summary>
    /// Registry of collision handlers keyed by (TypeA, TypeB).
    /// Internally stores compiled delegates that take (object a, object b)
    /// and do the appropriate typed call.
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

        public static void Register(HandlerDescriptor descriptor, bool alsoRegisterSymmetric = true)
        {
            if (descriptor is null)
                throw new ArgumentNullException(nameof(descriptor));

            var key = new HandlerKey(descriptor.ParamTypeA, descriptor.ParamTypeB);
            var list = _handlers.GetOrAdd(key, _ => new List<HandlerDescriptor>());
            lock (list)
            {
                list.Add(descriptor);
            }

            if (alsoRegisterSymmetric && descriptor.ParamTypeA != descriptor.ParamTypeB)
            {
                var symmetricKey = new HandlerKey(descriptor.ParamTypeB, descriptor.ParamTypeA);
                var symmetricList = _handlers.GetOrAdd(symmetricKey, _ => new List<HandlerDescriptor>());
                lock (symmetricList)
                {
                    symmetricList.Add(descriptor);
                }
            }
        }

        /// <summary>
        /// Returns registered handlers for the concrete type pair (aType, bType).
        /// This is intended for use in the collision dispatch path.
        /// </summary>
        public static IReadOnlyList<HandlerDescriptor> GetHandlers(Type aType, Type bType)
        {
            if (aType is null)
                throw new ArgumentNullException(nameof(aType));
            if (bType is null)
                throw new ArgumentNullException(nameof(bType));

            var key = new HandlerKey(aType, bType);
            return _handlers.TryGetValue(key, out var list)
                ? list
                : Array.Empty<HandlerDescriptor>();
        }

        public static int TotalHandlerCount => _handlers.Values.Sum(l => l.Count);
    }
}
