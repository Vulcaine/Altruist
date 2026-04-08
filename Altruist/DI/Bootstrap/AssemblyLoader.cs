using System.Reflection;
using System.Runtime.Loader;

using Microsoft.Extensions.DependencyModel;
using Microsoft.Extensions.Logging;

public static class AssemblyLoader
{
    /// <summary>
    /// Eagerly loads all assemblies referenced by the app (projects + NuGet),
    /// then also loads any *.dll from the output folder not already loaded.
    /// Safe to call multiple times.
    /// </summary>
    public static void EnsureAllReferencedAssembliesLoaded(ILogger? log = null)
    {
        var loadedNames = new HashSet<string>(
            AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .Select(a => a.GetName().Name!)
            , StringComparer.OrdinalIgnoreCase);

        // 1) Load assemblies declared in deps.json (projects + NuGet)
        var dc = DependencyContext.Default;
        if (dc != null)
        {
            foreach (var asmName in dc.GetDefaultAssemblyNames())
            {
                var name = asmName.Name!;
                if (!loadedNames.Add(name))
                    continue;

                try
                {
                    AssemblyLoadContext.Default.LoadFromAssemblyName(asmName);
                    log?.LogDebug("Loaded (deps): {Assembly}", name);
                }
                catch (Exception ex)
                {
                    // Not all entries are loadable in current context; skip quietly
                    log?.LogTrace(ex, "Skip load (deps): {Assembly}", name);
                }
            }
        }

        // 2) Load any physically present DLLs next to the app (covers copy-local/transitive)
        var baseDir = AppContext.BaseDirectory;
        foreach (var path in Directory.EnumerateFiles(baseDir, "*.dll"))
        {
            try
            {
                var an = AssemblyName.GetAssemblyName(path); // throws if not a .NET assembly
                if (!loadedNames.Add(an.Name!))
                    continue;

                AssemblyLoadContext.Default.LoadFromAssemblyPath(path);
                log?.LogDebug("Loaded (dir): {Assembly}", an.Name);
            }
            catch
            {
                // ignore native dlls or incompatible assemblies
            }
        }
    }
}
