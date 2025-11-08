// Altruist/AltruistServiceConfig.cs
using System.Globalization;
using System.Reflection;
using Altruist.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    public class AltruistServiceConfig : IAltruistConfiguration
    {
        private static readonly object _convLock = new();
        private static Dictionary<Type, IConfigConverter>? _converters;

        public void Configure(IServiceCollection services)
        {
            var config = AppConfigLoader.Load();

            var logger = services.BuildServiceProvider()
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger<AltruistServiceConfig>();

            EnsureConvertersDiscovered(services, logger);

            logger.LogInformation("📦 Loading services...");

            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                 .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                 .ToArray();

            var typesWithServiceAttr = TypeDiscovery.FindTypesWithAttribute<ServiceAttribute>(assemblies);

            var registeredServices = new List<string>();

            foreach (var implType in typesWithServiceAttr)
            {
                foreach (var svcAttr in implType.GetCustomAttributes<ServiceAttribute>())
                {
                    var serviceType = svcAttr.ServiceType ?? InferServiceType(implType);
                    if (serviceType == null)
                    {
                        logger.LogWarning("⚠️ Unable to infer service type for {Type}. Skipping registration.", implType.FullName);
                        continue;
                    }

                    services.Add(new ServiceDescriptor(
                        serviceType,
                        sp => CreateWithConfiguration(sp, config, implType, logger),
                        svcAttr.Lifetime));

                    var from = GetCleanName(serviceType);
                    var to = GetCleanName(implType);
                    registeredServices.Add($"\t{from} → {to} ({svcAttr.Lifetime})");
                }
            }

            if (registeredServices.Count > 0)
            {
                logger.LogInformation("✅ Registered services:\n{Services}", string.Join("\n", registeredServices));
            }
        }

        private static void EnsureConvertersDiscovered(IServiceCollection services, ILogger logger)
        {
            if (_converters is not null) return;

            lock (_convLock)
            {
                if (_converters is not null) return;

                var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                    .ToArray();

                var tempProvider = services.BuildServiceProvider();

                var map = new Dictionary<Type, IConfigConverter>();

                var converterTypes = TypeDiscovery.FindTypesWithAttribute<ConfigConverterAttribute>(assemblies).ToArray();
                foreach (var t in converterTypes)
                {
                    var attr = t.GetCustomAttribute<ConfigConverterAttribute>(false)!;

                    IConfigConverter? instance = null;

                    try
                    {
                        instance = (IConfigConverter?)ActivatorUtilities.CreateInstance(tempProvider, t);
                    }
                    catch
                    {
                        try { instance = (IConfigConverter?)Activator.CreateInstance(t); }
                        catch { }
                    }

                    if (instance is null)
                    {
                        logger.LogWarning("⚠️ Failed to create converter {Type} for {Target}.", t.FullName, attr.TargetType.Name);
                        continue;
                    }

                    if (instance.TargetType != attr.TargetType)
                    {
                        logger.LogWarning("⚠️ Converter {Type} target mismatch: attribute={AttrTarget}, converter={ConvTarget}. Using attribute value.",
                            t.FullName, attr.TargetType.Name, instance.TargetType.Name);
                        // prefer attribute TargetType for mapping
                        map[attr.TargetType] = instance;
                    }
                    else
                    {
                        map[attr.TargetType] = instance;
                    }
                }

                _converters = map;
                if (_converters.Count > 0)
                    logger.LogInformation("🔧 Discovered {Count} ConfigConverter(s).", _converters.Count);
            }
        }

        private static object CreateWithConfiguration(IServiceProvider sp, IConfiguration cfg, Type implType, ILogger logger)
        {
            var ctor = SelectConstructor(implType);

            var parameters = ctor.GetParameters();
            var args = new object?[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                var cfgAttr = p.GetCustomAttribute<ConfigValueAttribute>(inherit: false);

                if (cfgAttr is not null)
                {
                    args[i] = ResolveFromConfig(cfg, p.ParameterType, cfgAttr, logger);
                    continue;
                }

                var dep = sp.GetService(p.ParameterType);
                if (dep is null)
                {
                    throw new InvalidOperationException(
                        $"Unable to resolve service for type '{p.ParameterType}' while activating '{implType}'. " +
                        $"Parameter: '{p.Name}'. Add [ConfigValue] or register the dependency.");
                }
                args[i] = dep;
            }

            var instance = ctor.Invoke(args);

            foreach (var prop in implType.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!prop.CanWrite) continue;
                var cfgAttr = prop.GetCustomAttribute<ConfigValueAttribute>(inherit: false);
                if (cfgAttr is null) continue;

                var value = ResolveFromConfig(cfg, prop.PropertyType, cfgAttr, logger);
                prop.SetValue(instance, value);
            }

            return instance!;
        }

        private static ConstructorInfo SelectConstructor(Type implType)
        {
            var preferred = implType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(c => c.GetCustomAttribute<ActivatorUtilitiesConstructorAttribute>() is not null);

            if (preferred != null) return preferred;

            var selected = implType.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
                .OrderByDescending(c => c.GetParameters().Length)
                .FirstOrDefault();

            if (selected is null)
                throw new InvalidOperationException($"Type '{implType.FullName}' has no public constructors.");

            return selected;
        }

        private static object? ResolveFromConfig(IConfiguration cfg, Type targetType, ConfigValueAttribute attr, ILogger logger)
        {
            var section = cfg.GetSection(attr.Path);
            if (section.Exists())
            {
                if (!IsSimple(targetType))
                {
                    var obj = Activator.CreateInstance(targetType);
                    if (obj is null)
                        throw new InvalidOperationException($"Cannot create instance of {targetType} to bind configuration.");

                    section.Bind(obj);
                    return obj;
                }

                var raw = section.Value;
                if (raw is not null)
                    return ConvertTo(raw, targetType);
            }

            if (attr.Default is not null)
            {
                if (!IsSimple(targetType))
                {
                    var converted = TryConverter(attr.Default, targetType);
                    if (converted.success) return converted.value;

                    try
                    {
                        var json = System.Text.Json.JsonSerializer.Deserialize(attr.Default, targetType,
                            new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (json is not null) return json;
                    }
                    catch { }

                    throw new InvalidOperationException(
                        $"Cannot convert default '{attr.Default}' to complex type {targetType.Name}. " +
                        $"Provide JSON or a [ConfigConverter] for this type.");
                }

                return ConvertTo(attr.Default, targetType);
            }

            if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) is not null)
                return null;

            throw new InvalidOperationException(
                $"Missing configuration for '{attr.Path}' (target type {targetType.Name}) and no default provided.");
        }

        private static bool IsSimple(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;

            return type.IsPrimitive
                || type.IsEnum
                || type == typeof(string)
                || type == typeof(decimal)
                || type == typeof(DateTime)
                || type == typeof(DateTimeOffset)
                || type == typeof(TimeSpan)
                || type == typeof(Guid);
        }

        private static object? ConvertTo(string raw, Type targetType)
        {
            var underlying = Nullable.GetUnderlyingType(targetType) ?? targetType;

            var custom = TryConverter(raw, underlying);
            if (custom.success) return custom.value;

            if (underlying.IsEnum) return Enum.Parse(underlying, raw, ignoreCase: true);
            if (underlying == typeof(Guid)) return Guid.Parse(raw);
            if (underlying == typeof(TimeSpan)) return TimeSpan.Parse(raw, CultureInfo.InvariantCulture);
            if (underlying == typeof(DateTime)) return DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (underlying == typeof(DateTimeOffset)) return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (underlying == typeof(string)) return raw;

            // Normalize C#-style numeric literals: allow underscores and suffixes (f/F, d/D, m/M, u/U, l/L, ul/lu)
            static string NormalizeNumeric(string s, bool allowUnsignedLongCombo = true)
            {
                s = s.Trim().Replace("_", "");

                if (s.Length == 0) return s;

                // Strip single suffixes: f/F, d/D, m/M
                var last = char.ToLowerInvariant(s[^1]);
                if (last is 'f' or 'd' or 'm')
                    return s[..^1];

                // Strip combinations of u/U and l/L at the end (e.g., 123ul, 123LU)
                if (allowUnsignedLongCombo)
                {
                    int end = s.Length;
                    while (end > 0)
                    {
                        var c = char.ToLowerInvariant(s[end - 1]);
                        if (c == 'u' || c == 'l') end--;
                        else break;
                    }
                    if (end != s.Length) return s[..end];
                }

                return s;
            }

            if (underlying == typeof(float))
                return float.Parse(NormalizeNumeric(raw), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            if (underlying == typeof(double))
                return double.Parse(NormalizeNumeric(raw), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            if (underlying == typeof(decimal))
                return decimal.Parse(NormalizeNumeric(raw), NumberStyles.Number, CultureInfo.InvariantCulture);

            if (underlying == typeof(byte))
                return byte.Parse(NormalizeNumeric(raw, allowUnsignedLongCombo: false), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (underlying == typeof(sbyte))
                return sbyte.Parse(NormalizeNumeric(raw, allowUnsignedLongCombo: false), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (underlying == typeof(short))
                return short.Parse(NormalizeNumeric(raw, allowUnsignedLongCombo: false), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (underlying == typeof(ushort))
                return ushort.Parse(NormalizeNumeric(raw, allowUnsignedLongCombo: false), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (underlying == typeof(int))
                return int.Parse(NormalizeNumeric(raw, allowUnsignedLongCombo: false), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (underlying == typeof(uint))
                return uint.Parse(NormalizeNumeric(raw), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (underlying == typeof(long))
                return long.Parse(NormalizeNumeric(raw), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (underlying == typeof(ulong))
                return ulong.Parse(NormalizeNumeric(raw), NumberStyles.Integer, CultureInfo.InvariantCulture);

            return System.Convert.ChangeType(raw, underlying, CultureInfo.InvariantCulture);
        }

        private static (bool success, object? value) TryConverter(string raw, Type targetType)
        {
            var dict = _converters;
            if (dict is null) return (false, null);

            if (dict.TryGetValue(targetType, out var conv))
                return (true, conv.Convert(raw));

            return (false, null);
        }

        private static string GetCleanName(Type type)
        {
            if (type.IsGenericType)
            {
                var name = type.Name;
                var tickIndex = name.IndexOf('`');
                if (tickIndex > 0) name = name[..tickIndex];
                var genericArgs = type.GetGenericArguments().Select(GetCleanName);
                return $"{name}<{string.Join(", ", genericArgs)}>";
            }

            return type.Name;
        }

        private static Type InferServiceType(Type implementationType)
        {
            if (implementationType.IsGenericTypeDefinition)
            {
                throw new InvalidOperationException(
                    $"Cannot infer service type for open generic type '{implementationType.FullName}'. " +
                    $"Please specify the service type explicitly in the [Service] attribute.");
            }

            return implementationType;
        }
    }
}
