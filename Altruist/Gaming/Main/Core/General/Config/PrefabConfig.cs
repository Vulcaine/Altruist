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

using Altruist.Contracts;
using Altruist.Gaming.ThreeD;
using Altruist.Gaming.TwoD;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming
{
    /// <summary>
    /// Registers all concrete Prefab types (2D &amp; 3D) with DI, using the same dependency &amp; config
    /// binding logic as services. Prefabs are registered as Transient. After activation, a single
    /// [PostConstruct] public void method (if present) is invoked with DI/[ConfigValue]-resolved parameters.
    /// </summary>
    [ServiceConfiguration]
    public class PrefabConfig : IAltruistConfiguration
    {
        public Task Configure(IServiceCollection services)
        {
            var cfg = AppConfigLoader.Load();
            var logger = services.BuildServiceProvider()
                                 .GetRequiredService<ILoggerFactory>()
                                 .CreateLogger<PrefabConfig>();

            DependencyResolver.EnsureConverters(services, logger);

            var registered = new List<string>();

            foreach (var t in FindPrefabTypes())
            {
                if (!DependencyResolver.ShouldRegister(t, cfg, logger))
                    continue;

                services.Add(new ServiceDescriptor(
                    t,
                    sp =>
                    {
                        // Construct + bind config
                        var obj = DependencyResolver.CreateWithConfiguration(sp, cfg, t, logger);

                        // Enforce/Invoke [PostConstruct] if any
                        try
                        {
                            _ = DependencyResolver.InvokePostConstructAsync(obj, sp, cfg, logger);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "❌ PostConstruct failed on prefab {Type}.", t.FullName);
                            throw;
                        }

                        return obj!;
                    },
                    ServiceLifetime.Transient));

                registered.Add($"\t{DependencyResolver.GetCleanName(t)} → {DependencyResolver.GetCleanName(t)} (Transient) [Prefab]");
            }

            if (registered.Count > 0)
                logger.LogDebug("🎮 Registered prefabs:\n{Prefabs}", string.Join("\n", registered));

            return Task.CompletedTask;
        }

        private static IEnumerable<Type> FindPrefabTypes()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                               .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                               .ToArray();

            // We consider any non-abstract class deriving from Prefab2D or Prefab3D a prefab.
            var prefab2DBase = typeof(Prefab2D);
            var prefab3DBase = typeof(Prefab3D);

            foreach (var a in assemblies)
            {
                foreach (var t in a.GetTypes())
                {
                    if (!t.IsClass || t.IsAbstract)
                        continue;

                    // Accept subclasses of either 2D or 3D prefab base
                    if (prefab2DBase.IsAssignableFrom(t) || prefab3DBase.IsAssignableFrom(t))
                        yield return t;
                }
            }
        }
    }
}
