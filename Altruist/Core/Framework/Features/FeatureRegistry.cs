// Features/FeatureRegistry.cs
using System.Collections.Concurrent;
using System.Reflection;

namespace Altruist.Features
{
    public static class FeatureRegistry
    {
        private static readonly Dictionary<string, IAltruistFeatureProvider> _byId = new();

        public static void Register(IAltruistFeatureProvider provider)
            => _byId[provider.FeatureId] = provider;

        public static IAltruistFeatureProvider? Find(string id)
            => _byId.TryGetValue(id, out var p) ? p : null;

        // Call once after EnsureFeatureAssembliesLoaded()
        public static void AutoDiscover(Func<Assembly, bool>? assemblyFilter = null)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && (assemblyFilter?.Invoke(a) ?? true));

            foreach (var asm in asms)
            {
                Type[] types;
                try { types = asm.GetTypes(); } catch { continue; }

                foreach (var t in types)
                {
                    if (t.IsAbstract || t.IsInterface) continue;
                    if (!typeof(IAltruistFeatureProvider).IsAssignableFrom(t)) continue;
                    if (t.GetConstructor(Type.EmptyTypes) is null) continue;

                    var instance = (IAltruistFeatureProvider)Activator.CreateInstance(t)!;
                    Register(instance);
                }
            }
        }
    }
}
