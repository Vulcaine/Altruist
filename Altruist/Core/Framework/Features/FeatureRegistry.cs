// Features/FeatureRegistry.cs
using System.Reflection;

namespace Altruist.Features
{
    /// <summary>
    /// Static, process-wide lookup of <see cref="IAltruistFeatureProvider"/> instances by
    /// <see cref="IAltruistFeatureProvider.FeatureId"/>.
    /// </summary>
    /// <remarks>
    /// Not thread-safe: register/discover once during startup before any concurrent <see cref="Find"/>.
    /// Currently not invoked by the framework's own bootstrap (see <see cref="IAltruistFeatureProvider"/>).
    /// </remarks>
    public static class FeatureRegistry
    {
        private static readonly Dictionary<string, IAltruistFeatureProvider> _byId = new();

        /// <summary>Adds or replaces the provider under its <see cref="IAltruistFeatureProvider.FeatureId"/>.</summary>
        /// <param name="provider">Provider to register.</param>
        public static void Register(IAltruistFeatureProvider provider)
            => _byId[provider.FeatureId] = provider;

        /// <summary>Returns the provider registered under <paramref name="id"/> (case-sensitive), or null.</summary>
        /// <param name="id">Feature id.</param>
        public static IAltruistFeatureProvider? Find(string id)
            => _byId.TryGetValue(id, out var p) ? p : null;

        // Call once after EnsureFeatureAssembliesLoaded()
        /// <summary>
        /// Scans the assemblies already loaded in the current AppDomain (dynamic ones skipped) and registers a new
        /// instance of every concrete <see cref="IAltruistFeatureProvider"/> that has a public parameterless constructor.
        /// </summary>
        /// <remarks>
        /// Only already-loaded assemblies are scanned, so make sure feature assemblies are loaded first. Assemblies
        /// whose types cannot be loaded are skipped silently. Calling it again creates and re-registers fresh instances.
        /// </remarks>
        /// <param name="assemblyFilter">Optional predicate selecting which assemblies to scan; null scans all.</param>
        public static void AutoDiscover(Func<Assembly, bool>? assemblyFilter = null)
        {
            var asms = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && (assemblyFilter?.Invoke(a) ?? true));

            foreach (var asm in asms)
            {
                Type[] types;
                try
                { types = asm.GetTypes(); }
                catch { continue; }

                foreach (var t in types)
                {
                    if (t.IsAbstract || t.IsInterface)
                        continue;
                    if (!typeof(IAltruistFeatureProvider).IsAssignableFrom(t))
                        continue;
                    if (t.GetConstructor(Type.EmptyTypes) is null)
                        continue;

                    var instance = (IAltruistFeatureProvider)Activator.CreateInstance(t)!;
                    Register(instance);
                }
            }
        }
    }
}
