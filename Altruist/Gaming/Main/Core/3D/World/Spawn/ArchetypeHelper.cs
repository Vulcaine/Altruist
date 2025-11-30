/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

namespace Altruist.Gaming.ThreeD
{
    internal static class WorldObjectArchetypeHelper
    {
        private static readonly ConcurrentDictionary<Type, string> _cache = new();

        public static string ResolveArchetype(Type type)
        {
            if (type is null)
                throw new ArgumentNullException(nameof(type));

            return _cache.GetOrAdd(type, t =>
            {
                var attr = (WorldObjectAttribute?)Attribute.GetCustomAttribute(
                    t,
                    typeof(WorldObjectAttribute),
                    inherit: false);

                if (attr == null || string.IsNullOrWhiteSpace(attr.Archetype))
                {
                    return "";
                }

                return attr.Archetype;
            });
        }
    }
}
