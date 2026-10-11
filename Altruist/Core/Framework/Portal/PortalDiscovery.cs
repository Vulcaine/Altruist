// Altruist.Web/Features/PortalDiscovery.cs
namespace Altruist.Web.Features;

/// <summary>
/// Reflection scan for portal classes: every non-abstract class in the loaded assemblies carrying
/// <see cref="Altruist.PortalAttribute"/> with a non-empty endpoint.
/// </summary>
/// <remarks>
/// Used by the framework at startup to register portals in DI, map their transport routes and wire their
/// <see cref="Altruist.GateAttribute"/> handlers; application code rarely needs it. Only assemblies already loaded
/// into the AppDomain are scanned; assemblies that fail to load types are skipped silently.
/// </remarks>
public static class PortalDiscovery
{
    /// <summary>One discovered portal binding.</summary>
    /// <param name="PortalType">The portal class.</param>
    /// <param name="Path">The raw (not normalized) endpoint from the attribute.</param>
    public sealed record Descriptor(Type PortalType, string Path);

    /// <summary>Scans the loaded assemblies for portals. Not cached: each call reflects again.</summary>
    /// <returns>Distinct (type, path) pairs, ordered by type full name then path.</returns>
    public static IReadOnlyList<Descriptor> Discover()
    {
        var results = new List<Descriptor>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic))
        {
            Type[] types;
            try
            { types = asm.GetTypes(); }
            catch { continue; }

            foreach (var t in types)
            {
                if (!t.IsClass || t.IsAbstract)
                    continue;

                var attrs = t.GetCustomAttributes(typeof(PortalAttribute), inherit: false)
                             .Cast<PortalAttribute>();

                foreach (var attr in attrs)
                {
                    var path = attr.Endpoint; // map Endpoint -> Path
                    if (string.IsNullOrWhiteSpace(path))
                        continue;

                    var key = $"{t.FullName}|{path}";
                    if (seen.Add(key))
                        results.Add(new Descriptor(t, path));
                }
            }
        }

        return results
            .OrderBy(d => d.PortalType.FullName, StringComparer.Ordinal)
            .ThenBy(d => d.Path, StringComparer.Ordinal)
            .ToList();
    }
}
