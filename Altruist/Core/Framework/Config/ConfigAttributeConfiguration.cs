using System.Reflection;

using Altruist.Contracts;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Altruist
{
    public sealed class ConfigAttributeConfiguration : IAltruistConfiguration
    {
        public bool IsConfigured { get; set; }

        public async Task Configure(IServiceCollection services)
        {
            if (!services.Any(d => d.ServiceType == typeof(IServiceCollection)))
                services.AddSingleton(services);

            var assemblies = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                .ToArray();

            var candidates = TypeDiscovery
                .FindTypesWithAttribute<ServiceConfigurationAttribute>(assemblies)
                .Select(t => new { Type = t, Attr = t.GetCustomAttribute<ServiceConfigurationAttribute>()! })
                .OrderBy(x => x.Attr.Order)
                .ThenBy(x => x.Type.FullName)
                .ToArray();

            var cfg = AppConfigLoader.Load();

            using var tmp = services.BuildServiceProvider();
            var loggerFactory = tmp.GetRequiredService<ILoggerFactory>();
            var bootstrapLogger = loggerFactory.CreateLogger<ConfigAttributeConfiguration>();

            DependencyResolver.EnsureConverters(services, cfg, bootstrapLogger);

            foreach (var item in candidates)
            {
                if (!typeof(IAltruistConfiguration).IsAssignableFrom(item.Type))
                    continue;

                RegisterConfigType(services, cfg, item.Type, item.Attr, bootstrapLogger);
            }

            foreach (var item in candidates)
            {
                if (!typeof(IAltruistConfiguration).IsAssignableFrom(item.Type))
                    continue;

                await ConfigureConfigTypeAsync(services, cfg, item.Type, item.Attr, bootstrapLogger)
                    .ConfigureAwait(false);
            }

            IsConfigured = true;
        }

        public static void EnsureConfigurationRegisteredAndConfigured(
    IServiceCollection services,
    Type configType,
    IConfiguration cfg,
    ILogger log)
        {
            if (configType is null)
                throw new ArgumentNullException(nameof(configType));

            if (!typeof(IAltruistConfiguration).IsAssignableFrom(configType))
                return;

            var attr = configType.GetCustomAttribute<ServiceConfigurationAttribute>()
                       ?? new ServiceConfigurationAttribute();

            // Register if missing
            RegisterConfigType(services, cfg, configType, attr, log);

            // And configure synchronously
            ConfigureConfigTypeSync(services, cfg, configType, attr, log);
        }

        private static void ConfigureConfigTypeSync(
            IServiceCollection services,
            IConfiguration cfg,
            Type type,
            ServiceConfigurationAttribute attr,
            ILogger log)
        {
            ConfigureConfigTypeAsync(services, cfg, type, attr, log)
                .GetAwaiter()
                .GetResult();
        }

        // -----------------------------------------------------------------------------------
        //  FIX: Resolve full config path from wildcarded AppConfigValue("*:something")
        // -----------------------------------------------------------------------------------

        private static string ResolveWildcardPath(
            IConfiguration cfg,
            Type declaringType,
            AppConfigValueAttribute attr)
        {
            var star = attr.Path!.IndexOf('*');
            if (star < 0)
                return attr.Path!; // no wildcard

            var after = star + 1 < attr.Path.Length
                ? attr.Path[(star + 1)..].TrimStart(':')
                : string.Empty;

            // Determine instance root by inspecting ConditionalOnConfig(KeyField)
            var conds = declaringType.GetCustomAttributes<ConditionalOnConfigAttribute>().ToArray();
            var listAttr = conds.FirstOrDefault(c => !string.IsNullOrEmpty(c.KeyField));

            if (listAttr is null)
            {
                // fallback: treat cfg as root
                var rootPath = (cfg as IConfigurationSection)?.Path ?? "";
                return string.IsNullOrEmpty(after)
                    ? rootPath
                    : $"{rootPath}:{after}";
            }

            var listRoot = listAttr.Path;

            var section = cfg.GetSection(listRoot);
            var children = section.GetChildren().ToArray();

            // Determine which child corresponds to this instance
            foreach (var child in children)
            {
                var keyFieldVal = child[listAttr.KeyField!];
                if (keyFieldVal is null)
                    continue;

                // If this cfg is exactly this child, we found our root
                if (cfg is IConfigurationSection s &&
                    s.Path.StartsWith(child.Path, StringComparison.OrdinalIgnoreCase))
                {
                    return string.IsNullOrEmpty(after)
                        ? child.Path
                        : $"{child.Path}:{after}";
                }
            }

            // fallback: assume cfg is already correct root
            var fallbackRoot = (cfg as IConfigurationSection)?.Path ?? "";
            return string.IsNullOrEmpty(after)
                ? fallbackRoot
                : $"{fallbackRoot}:{after}";
        }


        private static Func<ParameterInfo, object?> CustomConfigResolver(
            IServiceProvider sp,
            IConfiguration cfg)
        {
            return param =>
            {
                var attr = param.GetCustomAttribute<AppConfigValueAttribute>();
                if (attr == null)
                    return null;

                var logger = sp.GetRequiredService<ILogger<ConfigAttributeConfiguration>>();

                var target = param.ParameterType;

                // FIX: Handle ILiveConfigValue<T>
                if (target.IsGenericType &&
                    target.GetGenericTypeDefinition() == typeof(ILiveConfigValue<>))
                {
                    var inner = target.GetGenericArguments()[0];
                    var declaring = param.Member.DeclaringType!;

                    // Compute absolute full path
                    var fullPath = ResolveWildcardPath(cfg, declaring, attr);

                    // Read initial value
                    var initial = DependencyResolver.ResolveFromConfig(
                        cfg,
                        inner,
                        new AppConfigValueAttribute(fullPath),
                        logger
                    );

                    // Create live wrapper
                    var liveType = typeof(LiveConfigValue<>).MakeGenericType(inner);
                    return Activator.CreateInstance(liveType, cfg, fullPath, initial);
                }

                // Non-live value fallback
                return DependencyResolver.ResolveFromConfig(
                    cfg, param.ParameterType, attr, logger);
            };
        }

        // -----------------------------------------------------------------------------------
        // Existing code unchanged below
        // -----------------------------------------------------------------------------------

        static void RegisterConfigType(
    IServiceCollection services,
    IConfiguration cfg,
    Type type,
    ServiceConfigurationAttribute attr,
    ILogger log)
        {
            if (!typeof(IAltruistConfiguration).IsAssignableFrom(type))
                return;

            if (!DependencyResolver.ShouldRegister(type, cfg, log))
                return;

            var serviceType = attr.ServiceType ?? type;
            var lifetime = attr.Lifetime;

            if (services.Any(d => d.ServiceType == serviceType))
                return;

            services.Add(new ServiceDescriptor(
                serviceType,
                sp =>
                {
                    var logger = sp.GetRequiredService<ILoggerFactory>().CreateLogger<ConfigAttributeConfiguration>();
                    DependencyResolver.EnsureConverters(services, cfg, logger);

                    // 🔥 Create a wrapper configuration that resolves wildcard paths correctly.
                    var wrappedCfg = new WildcardConfigWrapper(cfg, type);

                    // 🔥 Now use DependencyResolver normally — no modifications required.
                    return DependencyResolver.CreateWithConfiguration(
                        sp,
                        wrappedCfg,
                        type,
                        logger,
                        lifetime
                    );
                },
                lifetime));
        }

        static async Task ConfigureConfigTypeAsync(
            IServiceCollection services,
            IConfiguration cfg,
            Type type,
            ServiceConfigurationAttribute attr,
            ILogger log)
        {
            var serviceType = attr.ServiceType ?? type;

            using var sp = services.BuildServiceProvider();
            var instanceObj = sp.GetService(serviceType);
            if (instanceObj is null)
                return;

            if (instanceObj is not IAltruistConfiguration configInstance)
                return;

            if (configInstance.IsConfigured)
                return;

            await configInstance.Configure(services).ConfigureAwait(false);
            configInstance.IsConfigured = true;
        }
    }

    public sealed class WildcardConfigWrapper : IConfiguration
    {
        private readonly IConfiguration _inner;
        private readonly Type _declaring;

        public WildcardConfigWrapper(IConfiguration inner, Type declaring)
        {
            _inner = inner;
            _declaring = declaring;
        }

        public IConfigurationSection GetSection(string key)
        {
            // If key contains wildcard -> rewrite to full path
            if (key.Contains("*"))
            {
                key = ResolveWildcardPath(_inner, _declaring, new AppConfigValueAttribute(key));
            }

            return _inner.GetSection(key);
        }

        // Passthrough
        public IEnumerable<IConfigurationSection> GetChildren() => _inner.GetChildren();
        public IChangeToken GetReloadToken() => _inner.GetReloadToken();

        public string? this[string key]
        {
            get => _inner[key];
            set => _inner[key] = value;
        }

        private static string ResolveWildcardPath(
    IConfiguration cfg,
    Type declaringType,
    AppConfigValueAttribute attr)
        {
            if (attr.Path is null)
                return "";

            var starIndex = attr.Path.IndexOf('*');
            if (starIndex < 0)
                return attr.Path; // no wildcard → full path stays as-is

            // After the '*:' part
            string afterStar = starIndex + 1 < attr.Path.Length
                ? attr.Path[(starIndex + 1)..].TrimStart(':')
                : string.Empty;

            // Discover root from ConditionalOnConfig(..., KeyField = "id")
            var conds = declaringType.GetCustomAttributes<ConditionalOnConfigAttribute>().ToArray();
            var listAttr = conds.FirstOrDefault(c => !string.IsNullOrEmpty(c.KeyField));

            // If no list / wildcard metadata exists → fallback to cfg root
            if (listAttr is null)
            {
                var cfgRoot = (cfg as IConfigurationSection)?.Path ?? "";
                return string.IsNullOrEmpty(afterStar)
                    ? cfgRoot
                    : $"{cfgRoot}:{afterStar}";
            }

            var listRoot = listAttr.Path; // e.g., "altruist:game:worlds:items"
            var listSection = cfg.GetSection(listRoot);

            // Find children (items:0, items:1, ...)
            var children = listSection.GetChildren().ToArray();

            // Determine which child corresponds to THIS instance
            if (cfg is IConfigurationSection currentSection)
            {
                foreach (var child in children)
                {
                    // Match by section prefix (items:0, items:1...)
                    if (currentSection.Path.StartsWith(child.Path, StringComparison.OrdinalIgnoreCase))
                    {
                        return string.IsNullOrEmpty(afterStar)
                            ? child.Path                         // "*"
                            : $"{child.Path}:{afterStar}";       // "*:gravity"
                    }
                }
            }

            // Fallback if instance root cannot be inferred:
            var fallbackRoot = (cfg as IConfigurationSection)?.Path ?? "";

            return string.IsNullOrEmpty(afterStar)
                ? fallbackRoot
                : $"{fallbackRoot}:{afterStar}";
        }
    }

}
