using System.Reflection;
using Altruist.Gaming;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Benchmarks;

internal static class BenchmarkHelpers
{
    /// <summary>
    /// Resets <see cref="AIBehaviorDiscovery"/>'s static cache and re-discovers every
    /// [AIBehavior] in <paramref name="assembly"/>.
    /// </summary>
    public static void DiscoverAIBehaviors(Assembly assembly)
    {
        const BindingFlags flags = BindingFlags.Static | BindingFlags.NonPublic;
        typeof(AIBehaviorDiscovery).GetField("_discovered", flags)?.SetValue(null, false);
        if (typeof(AIBehaviorDiscovery).GetField("_templates", flags)?.GetValue(null) is System.Collections.IDictionary dict)
            dict.Clear();

        AIBehaviorDiscovery.DiscoverBehaviors(
            [assembly],
            t => Activator.CreateInstance(t)!,
            NullLoggerFactory.Instance.CreateLogger("bench"));
    }
}
