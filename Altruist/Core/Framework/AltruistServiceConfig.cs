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

using System.Globalization;
using System.Reflection;
using Altruist.Contracts;
using Altruist.Web.Features;
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
            var cfg = GetConfig();
            var logger = GetLogger(services);
            EnsureConverters(services, logger);
            var registered = new List<string>();
            RegisterServiceAttributes(services, cfg, logger, registered);
            RegisterPortals(services, cfg, logger, registered);
            LogRegistered(logger, registered);
        }

        // ---------- High-level helpers (≤5 lines each) ----------

        private static IConfiguration GetConfig() => AppConfigLoader.Load();

        private static ILogger GetLogger(IServiceCollection services) =>
            services.BuildServiceProvider().GetRequiredService<ILoggerFactory>().CreateLogger<AltruistServiceConfig>();

        private static Assembly[] GetAssemblies() =>
            AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName)).ToArray();

        private static IEnumerable<Type> Find<TAttr>() where TAttr : Attribute =>
            TypeDiscovery.FindTypesWithAttribute<TAttr>(GetAssemblies());

        private static void LogRegistered(ILogger logger, List<string> reg)
        { if (reg.Count > 0) logger.LogInformation("✅ Registered services:\n{Services}", string.Join("\n", reg)); }

        // ---------- Service & Portal registration ----------

        private static void RegisterServiceAttributes(IServiceCollection services, IConfiguration cfg, ILogger log, List<string> reg)
        {
            foreach (var t in Find<ServiceAttribute>())
                RegisterServiceType(services, cfg, log, reg, t);
        }

        private static void RegisterServiceType(IServiceCollection services, IConfiguration cfg, ILogger log, List<string> reg, Type implType)
        {
            if (!ShouldRegister(implType, cfg, log)) return;
            foreach (var svcAttr in implType.GetCustomAttributes<ServiceAttribute>())
                TryAddService(services, cfg, log, reg, implType, svcAttr);
        }

        private static void TryAddService(IServiceCollection services, IConfiguration cfg, ILogger log, List<string> reg, Type impl, ServiceAttribute attr)
        {
            var svcType = attr.ServiceType ?? InferServiceType(impl);
            if (svcType == null) { log.LogWarning("⚠️ Unable to infer service type for {Type}.", impl.FullName); return; }
            services.Add(new ServiceDescriptor(svcType, sp => CreateWithConfiguration(sp, cfg, impl, log), attr.Lifetime));
            reg.Add($"\t{GetCleanName(svcType)} → {GetCleanName(impl)} ({attr.Lifetime})");
        }

        private static void RegisterPortals(IServiceCollection services, IConfiguration cfg, ILogger log, List<string> reg)
        {
            var portals = PortalDiscovery.Discover().Select(d => d.PortalType).Distinct();
            foreach (var t in portals) RegisterPortalType(services, cfg, log, reg, t);
        }

        private static void RegisterPortalType(IServiceCollection services, IConfiguration cfg, ILogger log, List<string> reg, Type t)
        {
            if (!t.IsClass || t.IsAbstract) return;
            if (!ShouldRegister(t, cfg, log)) return;
            services.Add(new ServiceDescriptor(t, sp => CreateWithConfiguration(sp, cfg, t, log), ServiceLifetime.Transient));
            reg.Add($"\t{GetCleanName(t)} → {GetCleanName(t)} (Transient) [Portal]");
        }

        // ---------- Conditional registration ----------

        private static bool ShouldRegister(Type t, IConfiguration cfg, ILogger log)
        {
            var conds = t.GetCustomAttributes<ConditionalOnConfigAttribute>(false).ToArray();
            return conds.Length == 0 || conds.All(c => ConditionOk(c, cfg, log));
        }

        private static bool ConditionOk(ConditionalOnConfigAttribute c, IConfiguration cfg, ILogger log)
        {
            var s = cfg.GetSection(c.Path);
            if (string.IsNullOrEmpty(c.HavingValue)) return Exists(s, c, log);
            return EqualsValue(s, c, log);
        }

        private static bool Exists(IConfigurationSection s, ConditionalOnConfigAttribute c, ILogger log)
        { var ok = s.Exists(); if (!ok) log.LogDebug("ConditionalOnConfig missing: {Path}", c.Path); return ok; }

        private static bool EqualsValue(IConfigurationSection s, ConditionalOnConfigAttribute c, ILogger log)
        {
            var raw = s.Value; var ok = s.Exists() && raw is not null &&
                      string.Equals(raw.Trim(), c.HavingValue!.Trim(), c.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (!ok) log.LogDebug("ConditionalOnConfig mismatch: {Path}", c.Path);
            return ok;
        }

        // ---------- Converter discovery ----------

        private static void EnsureConverters(IServiceCollection services, ILogger log)
        {
            if (_converters is not null) return;
            lock (_convLock) { if (_converters is null) _converters = DiscoverConverters(services, log); }
        }

        private static Dictionary<Type, IConfigConverter> DiscoverConverters(IServiceCollection services, ILogger log)
        {
            var map = new Dictionary<Type, IConfigConverter>();
            var temp = services.BuildServiceProvider();
            foreach (var t in Find<ConfigConverterAttribute>()) TryAddConverter(map, temp, t, log);
            if (map.Count > 0) log.LogInformation("🔧 Discovered {Count} ConfigConverter(s).", map.Count);
            return map;
        }

        private static void TryAddConverter(Dictionary<Type, IConfigConverter> map, IServiceProvider sp, Type t, ILogger log)
        {
            var attr = t.GetCustomAttribute<ConfigConverterAttribute>(false)!;
            var inst = CreateConverter(sp, t);
            if (inst is null) { log.LogWarning("⚠️ Failed to create converter {Type} for {Target}.", t.FullName, attr.TargetType.Name); return; }
            map[attr.TargetType] = inst;
        }

        private static IConfigConverter? CreateConverter(IServiceProvider sp, Type t)
        {
            try { return (IConfigConverter?)ActivatorUtilities.CreateInstance(sp, t); }
            catch { try { return (IConfigConverter?)Activator.CreateInstance(t); } catch { return null; } }
        }

        // ---------- Activation with config ----------

        private static object CreateWithConfiguration(IServiceProvider sp, IConfiguration cfg, Type impl, ILogger log)
        {
            var ctor = SelectCtor(impl);
            var args = ctor.GetParameters().Select(p => Arg(sp, cfg, p, log)).ToArray();
            var obj = ctor.Invoke(args);
            BindConfigProps(cfg, log, impl, obj);
            return obj!;
        }

        private static ConstructorInfo SelectCtor(Type t) =>
            t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
             .OrderByDescending(c => c.GetCustomAttribute<ActivatorUtilitiesConstructorAttribute>() is null)
             .ThenByDescending(c => c.GetParameters().Length).First()
        ;

        private static object? Arg(IServiceProvider sp, IConfiguration cfg, ParameterInfo p, ILogger log)
        {
            var a = p.GetCustomAttribute<ConfigValueAttribute>(false);
            return a is not null ? ResolveFromConfig(cfg, p.ParameterType, a, log) :
                   sp.GetService(p.ParameterType) ?? throw new InvalidOperationException($"Unable to resolve '{p.ParameterType}' for '{p.Name}'.");
        }

        private static void BindConfigProps(IConfiguration cfg, ILogger log, Type t, object obj)
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            {
                var a = p.GetCustomAttribute<ConfigValueAttribute>(false);
                if (a is null) continue;
                p.SetValue(obj, ResolveFromConfig(cfg, p.PropertyType, a, log));
            }
        }

        // ---------- Config conversion ----------

        private static object? ResolveFromConfig(IConfiguration cfg, Type target, ConfigValueAttribute a, ILogger _)
        {
            var s = cfg.GetSection(a.Path);
            if (s.Exists()) return BindOrConvert(s, target);
            if (a.Default is not null) return DefaultTo(target, a.Default);
            return Nullable.GetUnderlyingType(target) is not null || !target.IsValueType
                ? null : throw new InvalidOperationException($"Missing configuration for '{a.Path}' (type {target.Name}).");
        }

        private static object? BindOrConvert(IConfigurationSection s, Type target)
        {
            if (!IsSimple(target)) return BindSection(s, target);
            var raw = s.Value; return raw is null ? null : ConvertTo(raw, target);
        }

        private static object BindSection(IConfigurationSection s, Type target)
        {
            var obj = Activator.CreateInstance(target)!; s.Bind(obj); return obj;
        }

        private static object? DefaultTo(Type target, string raw)
        {
            if (!IsSimple(target))
            {
                var c = TryConverter(raw, target);
                if (c.success) return c.value;

                try
                {
                    var opts = new System.Text.Json.JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    };
                    return System.Text.Json.JsonSerializer.Deserialize(raw, target, opts);
                }
                catch
                {
                    throw new InvalidOperationException($"Cannot convert default to complex type {target.Name}.");
                }
            }
            return ConvertTo(raw, target);
        }

        private static bool IsSimple(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
                   type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid);
        }

        private static object? ConvertTo(string raw, Type target)
        {
            var t = Nullable.GetUnderlyingType(target) ?? target;
            var c = TryConverter(raw, t); if (c.success) return c.value;
            if (t.IsEnum) return Enum.Parse(t, raw, true);
            if (t == typeof(Guid)) return Guid.Parse(raw);
            if (t == typeof(TimeSpan)) return TimeSpan.Parse(raw, CultureInfo.InvariantCulture);
            if (t == typeof(DateTime)) return DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (t == typeof(DateTimeOffset)) return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (t == typeof(string)) return raw;
            return ParseNumericOrChangeType(raw, t);
        }

        private static object ParseNumericOrChangeType(string raw, Type t)
        {
            if (t == typeof(float)) return float.Parse(Norm(raw), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            if (t == typeof(double)) return double.Parse(Norm(raw), NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            if (t == typeof(decimal)) return decimal.Parse(Norm(raw), NumberStyles.Number, CultureInfo.InvariantCulture);
            if (t == typeof(byte)) return byte.Parse(Norm(raw, false), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (t == typeof(sbyte)) return sbyte.Parse(Norm(raw, false), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (t == typeof(short)) return short.Parse(Norm(raw, false), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (t == typeof(ushort)) return ushort.Parse(Norm(raw, false), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (t == typeof(int)) return int.Parse(Norm(raw, false), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (t == typeof(uint)) return uint.Parse(Norm(raw), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (t == typeof(long)) return long.Parse(Norm(raw), NumberStyles.Integer, CultureInfo.InvariantCulture);
            if (t == typeof(ulong)) return ulong.Parse(Norm(raw), NumberStyles.Integer, CultureInfo.InvariantCulture);
            return System.Convert.ChangeType(raw, t, CultureInfo.InvariantCulture)!;
        }

        private static string Norm(string s, bool allowUL = true)
        {
            s = s.Trim().Replace("_", "");
            if (s.Length == 0) return s;
            var last = char.ToLowerInvariant(s[^1]);
            if (last is 'f' or 'd' or 'm') return s[..^1];
            if (!allowUL) return s;
            int end = s.Length; while (end > 0 && "ul".Contains(char.ToLowerInvariant(s[end - 1]))) end--;
            return end != s.Length ? s[..end] : s;
        }

        private static (bool success, object? value) TryConverter(string raw, Type t)
        {
            var dict = _converters; if (dict is null) return (false, null);
            return dict.TryGetValue(t, out var conv) ? (true, conv.Convert(raw)) : (false, null);
        }

        // ---------- Misc ----------

        private static string GetCleanName(Type type)
        {
            if (type.IsGenericType)
            {
                var name = type.Name; var i = name.IndexOf('`'); if (i > 0) name = name[..i];
                var args = type.GetGenericArguments().Select(GetCleanName);
                return $"{name}<{string.Join(", ", args)}>";
            }
            return type.Name;
        }

        private static Type InferServiceType(Type impl)
        {
            if (impl.IsGenericTypeDefinition)
                throw new InvalidOperationException($"Cannot infer service type for open generic '{impl.FullName}'.");
            return impl;
        }
    }
}
