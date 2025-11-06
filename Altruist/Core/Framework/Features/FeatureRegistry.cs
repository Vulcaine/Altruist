// Features/FeatureRegistry.cs
using System.Collections.Concurrent;

namespace Altruist.Features
{
    public static class FeatureRegistry
    {
        private static readonly ConcurrentBag<IAltruistFeatureProvider> _providers = new();

        public static void Register(IAltruistFeatureProvider provider) => _providers.Add(provider);

        public static IAltruistFeatureProvider? Find(string featureId) =>
            _providers.FirstOrDefault(p => string.Equals(p.FeatureId, featureId, StringComparison.OrdinalIgnoreCase));
    }
}
