// Altruist.Web/Features/PortalDiscovery.cs
namespace Altruist.Web.Features;

public static class PortalDiscovery
{
    public sealed record Descriptor(Type PortalType, string Path);

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
