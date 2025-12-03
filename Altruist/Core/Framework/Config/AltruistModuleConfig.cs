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

        /// <summary>
        /// Only binds [ConfigurationProperties] classes to the IServiceCollection.
        /// Module loaders are executed later via RunModulesAsync, after the container is built.
        /// </summary>
        public Task Configure(IServiceCollection services)
        {
            // We still grab a temporary logger for binding diagnostics
            using var tmpProvider = services.BuildServiceProvider();
            var logger = tmpProvider
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger<AltruistModuleConfig>();

            BindConfigurationClasses(services, logger);

            IsConfigured = true;
            return Task.CompletedTask;
        }

        /// <summary>
        /// Discover and execute all [AltruistModule]/[AltruistModuleLoader] methods
        /// AFTER all services are registered and the root IServiceProvider is built.
        ///
        /// Loader methods:
        /// - must be public static
        /// - must return void or Task
        /// - may take any number of parameters, all resolved from DI
        /// </summary>
        public static async Task RunModulesAsync(IServiceProvider provider)
        {
            var loggerFactory = provider.GetRequiredService<ILoggerFactory>();
            var logger = loggerFactory.CreateLogger<AltruistModuleConfig>();

            logger.LogDebug("🧩 Discovering Altruist modules (post-service registration)...");

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
                return;
            }

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
                    var returnsTask = typeof(Task).IsAssignableFrom(m.ReturnType);

                    if (!returnsVoid && !returnsTask)
                    {
                        logger.LogWarning("⚠️ {Module}.{Method} must return void or Task. Skipping.", moduleName, m.Name);
                        continue;
                    }

                    var parameters = m.GetParameters();
                    object?[] args = new object?[parameters.Length];
                    var canInvoke = true;

                    // Resolve dependencies from DI
                    for (int i = 0; i < parameters.Length; i++)
                    {
                        var p = parameters[i];
                        var dep = provider.GetService(p.ParameterType);
                        if (dep is null)
                        {
                            logger.LogError(
                                "❌ Cannot resolve dependency {ParamType} for {Module}.{Method}. Skipping.",
                                p.ParameterType.FullName,
                                moduleName,
                                m.Name);

                            canInvoke = false;
                            break;
                        }

                        args[i] = dep;
                    }

                    if (!canInvoke)
                        continue;

                    try
                    {
                        var result = m.Invoke(obj: null, parameters: args);

                        if (returnsTask && result is Task task)
                            await task;

                        invoked.Add($"{moduleName}.{m.Name}({string.Join(", ", parameters.Select(p => p.ParameterType.Name))})");
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
            {
                logger.LogDebug("✅ Invoked module loaders:\n{List}",
                    string.Join("\n", invoked.Select(s => "\t" + s)));
            }
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
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                return ex.Types.Where(t => t is not null)!;
            }
        }
    }
}
