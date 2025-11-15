// Altruist/Modules/AltruistModuleConfig.cs
using System.Reflection;

using Altruist.Contracts;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    public class AltruistModuleConfig : IAltruistConfiguration
    {
        public bool IsConfigured { get; set; }

        public async Task Configure(IServiceCollection services)
        {
            var logger = services.BuildServiceProvider()
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger<AltruistModuleConfig>();

            logger.LogDebug("🧩 Discovering Altruist modules...");

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                .ToArray();

            var modules = assemblies
                .SelectMany(SafeGetTypes)
                .Where(t => t.IsClass && t.GetCustomAttribute<AltruistModuleAttribute>(inherit: false) is not null)
                .ToArray();

            if (modules.Length == 0)
            {
                logger.LogDebug("ℹ️ No modules annotated with [AltruistModule] found.");
            }
            else
            {
                var invoked = new List<string>();
                foreach (var type in modules)
                {
                    // Require public static class
                    if (!(type.IsAbstract && type.IsSealed) || !(type.IsPublic || type.IsNestedPublic))
                    {
                        logger.LogWarning("⚠️ Type '{Type}' has [AltruistModule] but is not a public static class. Skipping.", type.FullName);
                        continue;
                    }

                    var typeAttr = type.GetCustomAttribute<AltruistModuleAttribute>(inherit: false);
                    var moduleName = typeAttr?.Name ?? type.FullName ?? type.Name;

                    var loaders = type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                        .Where(m => m.GetCustomAttribute<AltruistModuleLoaderAttribute>(inherit: false) is not null)
                        .ToArray();

                    if (loaders.Length == 0)
                    {
                        logger.LogWarning("⚠️ Module '{Module}' defines no [AltruistModuleLoader] methods. Skipping.", moduleName);
                        continue;
                    }

                    foreach (var m in loaders)
                    {
                        var returnsVoid = m.ReturnType == typeof(void);
                        var returnsTask = m.ReturnType == typeof(Task);

                        if (!returnsVoid && !returnsTask)
                        {
                            logger.LogWarning("⚠️ {Module}.{Method} must return void or Task. Skipping.", moduleName, m.Name);
                            continue;
                        }

                        if (m.GetParameters().Length != 0)
                        {
                            logger.LogWarning("⚠️ {Module}.{Method} must be parameterless. Skipping.", moduleName, m.Name);
                            continue;
                        }

                        try
                        {
                            var result = m.Invoke(obj: null, parameters: null);
                            if (returnsTask && result is Task task)
                                await task;

                            invoked.Add($"{moduleName}.{m.Name}()");
                        }
                        catch (TargetInvocationException tie)
                        {
                            logger.LogError(tie.InnerException ?? tie, "❌ Exception while invoking {Module}.{Method}.", moduleName, m.Name);
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "❌ Failed to invoke {Module}.{Method}.", moduleName, m.Name);
                        }
                    }
                }

                if (invoked.Count > 0)
                    logger.LogDebug("✅ Invoked module loaders:\n{List}", string.Join("\n", invoked.Select(s => "\t" + s)));
            }

            BindConfigurationClasses(services, logger);
        }

        private static void BindConfigurationClasses(IServiceCollection services, ILogger logger)
        {
            var cfg = AppConfigLoader.Load();

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                .ToArray();

            var cfgTypes = assemblies
                .SelectMany(SafeGetTypes)
                .Where(t => t.IsClass && !t.IsAbstract && t.GetCustomAttribute<ConfigurationPropertiesAttribute>(false) is not null)
                .ToArray();

            if (cfgTypes.Length == 0)
            {
                logger.LogDebug("ℹ️ No classes annotated with [ConfigurationProperties] found.");
                return;
            }

            foreach (var t in cfgTypes)
            {
                var attr = t.GetCustomAttribute<ConfigurationPropertiesAttribute>(false)!;
                var section = cfg.GetSection(attr.Path);

                if (!section.Exists())
                {
                    logger.LogDebug("Config section '{Path}' not found for type {Type}. Skipping.", attr.Path, t.FullName);
                    continue;
                }

                var looksArray = section.GetChildren().Any(c => int.TryParse(c.Key, out _));
                if (looksArray)
                {
                    var listType = typeof(List<>).MakeGenericType(t);
                    var listInstance = section.Get(listType);
                    if (listInstance is null)
                    {
                        logger.LogWarning("Failed to bind list at '{Path}' to {Type}.", attr.Path, listType);
                        continue;
                    }

                    services.AddSingleton(listType, listInstance);
                    var ienumType = typeof(IEnumerable<>).MakeGenericType(t);
                    var ireadOnlyListType = typeof(IReadOnlyList<>).MakeGenericType(t);
                    services.AddSingleton(ienumType, sp => sp.GetRequiredService(listType));
                    services.AddSingleton(ireadOnlyListType, sp => sp.GetRequiredService(listType));
                    logger.LogDebug("🔧 Bound config array '{Path}' to {Type}.", attr.Path, listType.Name);
                }
                else
                {
                    var instance = Activator.CreateInstance(t);
                    if (instance is null)
                    {
                        logger.LogWarning("Failed to create instance of {Type} for binding.", t.FullName);
                        continue;
                    }

                    section.Bind(instance);
                    services.AddSingleton(t, instance);
                    logger.LogDebug("🔧 Bound config '{Path}' to {Type}.", attr.Path, t.Name);
                }
            }
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try
            { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t is not null)!; }
        }
    }
}
