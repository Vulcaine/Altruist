/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Reflection;
using System.Runtime.CompilerServices;

using Microsoft.Extensions.DependencyInjection;

public static class IServiceProviderExtensions
{
    public static IEnumerable<TType> GetAll<TType>(this IServiceProvider provider)
    {
        if (provider is null)
            throw new ArgumentNullException(nameof(provider));

        var descriptors = GetServiceDescriptors(provider).ToArray();
        if (descriptors.Length == 0)
            return Enumerable.Empty<TType>();

        var results = new List<TType>();

        foreach (var descriptor in descriptors)
        {
            try
            {
                var instance = provider.GetService(descriptor.ServiceType);
                if (instance is TType typed)
                {
                    results.Add(typed);
                }
            }
            catch
            {
                // swallow – we only want successfully constructed services
            }
        }

        return results;
    }

    private static IEnumerable<ServiceDescriptor> GetServiceDescriptors(IServiceProvider provider)
    {
        // We may have wrapper providers around the real one, so search
        // the provider and any nested IServiceProvider properties.
        var visited = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var queue = new Queue<object>();
        queue.Enqueue(provider);

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (current is null || !visited.Add(current))
                continue;

            var t = current.GetType();

            // 1) Try a ServiceDescriptors property (public or non-public)
            var sdProp = t.GetProperty(
                "ServiceDescriptors",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (sdProp != null)
            {
                try
                {
                    if (sdProp.GetValue(current) is IEnumerable<ServiceDescriptor> enumerableFromProp)
                        return enumerableFromProp;
                }
                catch
                {
                    // ignore and fall through to CallSiteFactory path
                }
            }

            // 2) Try CallSiteFactory + _descriptors (the existing approach)
            var callSiteFactoryProp = t.GetProperty(
                "CallSiteFactory",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (callSiteFactoryProp != null)
            {
                try
                {
                    var callSiteFactory = callSiteFactoryProp.GetValue(current);
                    if (callSiteFactory != null)
                    {
                        var descField = callSiteFactory
                            .GetType()
                            .GetField("_descriptors", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

                        if (descField != null)
                        {
                            if (descField.GetValue(callSiteFactory) is IEnumerable<ServiceDescriptor> enumerableFromField)
                                return enumerableFromField;
                        }
                    }
                }
                catch
                {
                    // ignore – we'll keep trying nested providers
                }
            }

            // 3) Enqueue nested IServiceProvider properties (wrappers, scopes, etc.)
            foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!typeof(IServiceProvider).IsAssignableFrom(prop.PropertyType))
                    continue;

                try
                {
                    var nested = prop.GetValue(current);
                    if (nested != null)
                        queue.Enqueue(nested);
                }
                catch
                {
                    // ignore bad getters
                }
            }
        }

        // Could not find descriptors anywhere
        return Array.Empty<ServiceDescriptor>();
    }

    /// <summary>
    /// Reference-equality comparer for walking provider graphs.
    /// </summary>
    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();

        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
