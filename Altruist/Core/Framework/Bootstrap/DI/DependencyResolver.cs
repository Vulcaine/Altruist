/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Globalization;
using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    /// <summary>
    /// Shared dependency creation & config-binding utilities reused by service & prefab registration.
    /// Centralizes: constructor selection, config arg/property binding, custom converters, and conditional registration.
    /// Also supports invoking a single [PostConstruct] public void instance method with resolved parameters.
    /// </summary>
    public static class DependencyResolver
    {
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, object> _singletonCache
            = new System.Collections.Concurrent.ConcurrentDictionary<Type, object>();

        private static readonly object _convLock = new();
        private static Dictionary<Type, IConfigConverter>? _converters;

        // ---- Circular construction tracking (per-async-flow) ----
        private static readonly AsyncLocal<Stack<Type>> _constructionPath = new();

        private static Stack<Type> GetConstructionStack()
        {
            var s = _constructionPath.Value;
            if (s is null)
            {
                s = new Stack<Type>();
                _constructionPath.Value = s;
            }
            return s;
        }

        private static string FormatCyclePath(IEnumerable<Type> path, Type repeat)
        {
            // path is a stack (LIFO). We want first->last pretty string.
            var seq = path.Reverse().Concat(new[] { repeat }).Select(GetCleanName);
            return string.Join(" → ", seq);
        }

        // --------------------------- Public API ---------------------------

        /// <summary>Make sure custom converters are discovered exactly once.</summary>
        public static void EnsureConverters(IServiceCollection services, ILogger log)
        {
            if (_converters is not null)
                return;
            lock (_convLock)
            {
                if (_converters is null)
                    _converters = DiscoverConverters(services, log);
            }
        }

        public static object CreateWithConfiguration(IServiceProvider sp, IConfiguration cfg, Type impl, ILogger log)
            => CreateWithConfiguration(sp, cfg, impl, log, ServiceLifetime.Singleton);

        public static object CreateWithConfiguration(IServiceProvider sp, IConfiguration cfg, Type impl, ILogger log, ServiceLifetime lifetime)
        {
            if (lifetime == ServiceLifetime.Singleton)
            {
                // One instance per AppDomain per impl type
                return _singletonCache.GetOrAdd(
                    impl,
                    _ => CreateInstanceInternal(sp, cfg, impl, log));
            }

            // Transient / other lifetimes
            return CreateInstanceInternal(sp, cfg, impl, log);
        }

        private static object CreateInstanceInternal(IServiceProvider sp, IConfiguration cfg, Type impl, ILogger log)
        {
            var path = GetConstructionStack();
            if (path.Contains(impl))
            {
                // try container first (may already have been created/registered)
                var resolved = sp.GetService(impl);
                if (resolved is not null)
                    return resolved;

                var cycle = FormatCyclePath(path, impl);
                throw new InvalidOperationException(
                    $"Circular dependency detected while creating {GetCleanName(impl)}. Path: {cycle}");
            }

            path.Push(impl);
            try
            {
                var ctor = SelectCtor(impl);
                var args = ctor.GetParameters().Select(p => Arg(sp, cfg, p, log)).ToArray();
                var obj = ctor.Invoke(args);
                BindConfigProps(cfg, log, impl, obj);
                return obj!;
            }
            finally
            {
                _ = path.Pop();
            }
        }

        /// <summary>Return true if the type should be registered given ConditionalOnConfig attributes.</summary>
        public static bool ShouldRegister(Type t, IConfiguration cfg, ILogger log)
        {
            var conds = t.GetCustomAttributes<ConditionalOnConfigAttribute>(false).ToArray();
            return conds.Length == 0 || conds.All(c => ConditionOk(c, cfg, log));
        }

        /// <summary>
        /// Try to infer a default service type for registration. Falls back to the implementation itself.
        /// Throws for open generics.
        /// </summary>
        public static Type InferServiceType(Type impl)
        {
            if (impl.IsGenericTypeDefinition)
                throw new InvalidOperationException($"Cannot infer service type for open generic '{impl.FullName}'.");
            return impl;
        }

        /// <summary>Format a readable, generic-aware type name for logs.</summary>
        public static string GetCleanName(Type type)
        {
            if (type.IsGenericType)
            {
                var name = type.Name;
                var i = name.IndexOf('`');
                if (i > 0)
                    name = name[..i];
                var args = type.GetGenericArguments().Select(GetCleanName);
                return $"{name}<{string.Join(", ", args)}>";
            }
            return type.Name;
        }

        /// <summary>
        /// Find and invoke the single allowed [PostConstruct] method on the given instance.
        /// Rules enforced:
        ///  - at most ONE [PostConstruct] method per type,
        ///  - it MUST be a public instance method,
        ///  - it MUST return void,
        ///  - arguments are resolved via DI and/or [ConfigValue] just like constructor parameters.
        /// If no such method exists, this is a no-op.
        /// </summary>
        public static async Task InvokePostConstructAsync(object instance, IServiceProvider sp, IConfiguration cfg, ILogger log)
        {
            if (instance is null)
                throw new ArgumentNullException(nameof(instance));
            var type = instance.GetType();

            var methods = type
                .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic)
                .Where(m => m.GetCustomAttribute<PostConstructAttribute>(inherit: true) is not null)
                .ToArray();

            if (methods.Length == 0)
                return;
            if (methods.Length > 1)
                throw new InvalidOperationException($"Type '{type.FullName}' declares multiple [PostConstruct] methods. Only one is allowed.");

            var m = methods[0];

            if (!m.IsPublic || m.IsStatic)
                throw new InvalidOperationException($"[PostConstruct] method '{type.FullName}.{m.Name}' must be a public instance method.");

            var rt = m.ReturnType;
            var isVoid = rt == typeof(void);
            var isTask = rt == typeof(Task);
            var isValueTask = rt == typeof(ValueTask);

            if (!isVoid && !isTask && !isValueTask)
                throw new InvalidOperationException($"[PostConstruct] '{type.FullName}.{m.Name}' must return void, Task, or ValueTask.");

            var args = m.GetParameters().Select(p => Arg(sp, cfg, p, log)).ToArray();

            try
            {
                var result = m.Invoke(instance, args);
                if (isTask)
                    await (Task)result!;
                else if (isValueTask)
                    await (ValueTask)result!;
            }
            catch (TargetInvocationException tie) when (tie.InnerException is not null)
            {
                log.LogError(tie.InnerException, "PostConstruct method {Method} on {Type} threw.", m.Name, type.FullName);
                throw;
            }
        }

        // optional convenience
        public static async Task<T> CreateWithPostConstructAsync<T>(IServiceProvider sp, IConfiguration cfg, ILogger log)
        {
            var instance = ActivatorUtilities.CreateInstance<T>(sp)!;
            await InvokePostConstructAsync(instance!, sp, cfg, log);
            return instance;
        }

        // ---------------------- Internal helpers -------------------------

        private static ConstructorInfo SelectCtor(Type t) =>
            t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
             // prefer the one marked with [ActivatorUtilitiesConstructor] if present
             .OrderByDescending(c => c.GetCustomAttribute<ActivatorUtilitiesConstructorAttribute>() is not null)
             // otherwise prefer the "widest" public ctor
             .ThenByDescending(c => c.GetParameters().Length)
             .First();

        private static object? Arg(IServiceProvider sp, IConfiguration cfg, ParameterInfo p, ILogger log)
        {
            var paramType = p.ParameterType;

            var a = p.GetCustomAttribute<AppConfigValueAttribute>(false);
            if (a is not null)
                return ResolveFromConfig(cfg, p.ParameterType, a, log);

            // 0) Hard-stop for simple/BCL types (string, primitives, etc.).
            // These must be bound via [AppConfigValue] or given a default.
            if (IsSimple(paramType))
            {
                var owner = GetCleanName(p.Member.DeclaringType!);
                var pn = p.Name ?? "param";
                var tn = GetCleanName(paramType);
                var errMsg =
                    $"❌ Parameter '{pn}' of '{owner}' is a simple type '{tn}'. " +
                    "Bind it from configuration with [AppConfigValue] or provide a default value.";
                FailAndExit(log, errMsg);
                throw new InvalidOperationException(errMsg);
            }

            // 2) Handle all supported collection kinds (Spring-style)
            if (paramType.IsGenericType)
            {
                var genDef = paramType.GetGenericTypeDefinition();
                var elemType = paramType.GetGenericArguments()[0];

                if (genDef == typeof(IEnumerable<>) ||
                    genDef == typeof(IList<>) ||
                    genDef == typeof(ICollection<>) ||
                    genDef == typeof(IReadOnlyList<>) ||
                    genDef == typeof(List<>) ||
                    genDef == typeof(HashSet<>))
                {
                    var servicesEnumObj = ServiceProviderServiceExtensions.GetServices(sp, elemType);
                    var resultCollection = CreateCollectionOf(elemType, genDef, servicesEnumObj);
                    return resultCollection;
                }
            }

            // 3) Arrays: T[]
            if (paramType.IsArray)
            {
                var elemType = paramType.GetElementType()!;
                var servicesEnumObj = ServiceProviderServiceExtensions
                    .GetServices(sp, elemType)
                    .Cast<object>()
                    .ToArray();

                var arr = Array.CreateInstance(elemType, servicesEnumObj.Length);
                for (int i = 0; i < servicesEnumObj.Length; i++)
                    arr.SetValue(servicesEnumObj[i], i);

                return arr;
            }

            // 4) Try resolve the exact service
            var service = sp.GetService(paramType);
            if (service is not null)
                return service;

            // 5) Optional/default value?
            if (p.HasDefaultValue)
                return p.DefaultValue;

            // 6) Nullable value types (T?) → default(T?)
            if (Nullable.GetUnderlyingType(paramType) is not null)
                return null;

            // 7) Fallback default(T) for structs
            if (paramType.IsValueType)
                return Activator.CreateInstance(paramType);

            // 8) Truly unresolved reference type → analyze & throw smart error
            var implName = GetCleanName(paramType);
            var ctorOwner = GetCleanName(p.Member.DeclaringType!);

            var diagnostics = BuildConditionalDiagnostics(paramType, cfg, log);

            var msg =
                $"❌ Unable to resolve required dependency '{implName}' for constructor parameter '{p.Name}' in type '{ctorOwner}'.\n" +
                diagnostics +
                $"👉 Make sure an implementation is registered or annotate one with [Service(typeof({implName}))].";

            FailAndExit(log, msg);
            throw new InvalidOperationException(msg);
        }

        /// <summary>
        /// If the requested dependency type matches (or could match) a service
        /// that was filtered out by [ConditionalOnConfig], suggest the config keys.
        /// </summary>
        private static string BuildConditionalDiagnostics(Type missingType, IConfiguration cfg, ILogger log)
        {
            var allConditional = FindConditionallyFilteredTypes();
            var matches = allConditional
                .Where(t => missingType.IsAssignableFrom(t.Type))
                .ToArray();

            if (matches.Length == 0)
                return string.Empty;

            var sb = new System.Text.StringBuilder();
            sb.AppendLine("🔍 Found conditional services that match this type but were not enabled:");

            foreach (var m in matches)
            {
                sb.AppendLine($"   • {GetCleanName(m.Type)}");

                foreach (var cond in m.Conditions)
                {
                    bool exists = cfg.GetSection(cond.Path).Exists();
                    string status = exists ? "✔️ Found" : "❌ Missing";
                    string expected = string.IsNullOrEmpty(cond.HavingValue)
                        ? "(must exist)"
                        : $"= \"{cond.HavingValue}\"";

                    sb.AppendLine($"      - Config: {cond.Path} {expected} → {status}");
                }
            }

            sb.AppendLine();
            sb.AppendLine("🧭 To enable, add the missing keys to your configuration.");
            sb.AppendLine();

            return sb.ToString();
        }

        /// <summary>
        /// Logs a critical dependency resolution failure and terminates the process.
        /// </summary>
        private static void FailAndExit(ILogger log, string message, Exception? ex = null)
        {
            try
            {
                if (ex is not null)
                    log.LogCritical(ex, message);
                else
                    log.LogCritical(message);

                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("💀 FATAL: " + message);
                Console.ResetColor();

                // Give the logger a moment to flush
                System.Threading.Thread.Sleep(200);

                // Ensure a clean exit
                Environment.Exit(1);
            }
            catch
            {
                // As a last resort, try to exit immediately
                try
                { Environment.FailFast(message, ex); }
                catch { /* ignore */ }
            }
        }

        /// <summary>
        /// Discover all types marked with [ConditionalOnConfig].
        /// Returns tuples of (Type, List of Conditions).
        /// </summary>
        private static List<(Type Type, List<ConditionalOnConfigAttribute> Conditions)> FindConditionallyFilteredTypes()
        {
            var assemblies = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                .ToArray();

            var results = new List<(Type, List<ConditionalOnConfigAttribute>)>();
            foreach (var t in assemblies.SelectMany(a => a.GetTypes()))
            {
                var attrs = t.GetCustomAttributes<ConditionalOnConfigAttribute>(false).ToList();
                if (attrs.Count > 0)
                    results.Add((t, attrs));
            }
            return results;
        }

        private static object CreateCollectionOf(Type elemType, Type genDef, IEnumerable<object> services)
        {
            // Convert enumerable to array first
            var elements = services.ToList();

            // Handle HashSet<T> explicitly
            if (genDef == typeof(HashSet<>))
            {
                var setType = typeof(HashSet<>).MakeGenericType(elemType);
                var set = Activator.CreateInstance(setType)!;

                var addMethod = setType.GetMethod("Add", new[] { elemType })!;
                foreach (var s in elements)
                    addMethod.Invoke(set, new[] { s });

                return set;
            }

            // Default: List<T> (covers IEnumerable, IList, ICollection, IReadOnlyList)
            var listType = typeof(List<>).MakeGenericType(elemType);
            var list = Activator.CreateInstance(listType)!;

            var addMethod2 = listType.GetMethod("Add", new[] { elemType })!;
            foreach (var s in elements)
                addMethod2.Invoke(list, new[] { s });

            return list;
        }

        private static void BindConfigProps(IConfiguration cfg, ILogger log, Type t, object obj)
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
            {
                var a = p.GetCustomAttribute<AppConfigValueAttribute>(false);
                if (a is null)
                    continue;
                p.SetValue(obj, ResolveFromConfig(cfg, p.PropertyType, a, log));
            }
        }

        private static bool ConditionOk(ConditionalOnConfigAttribute c, IConfiguration cfg, ILogger log)
        {
            var s = cfg.GetSection(c.Path);
            if (string.IsNullOrEmpty(c.HavingValue))
                return Exists(s, c, log);

            return EqualsValue(s, c, log);
        }

        private static bool Exists(IConfigurationSection s, ConditionalOnConfigAttribute c, ILogger log)
        {
            var ok = s.Exists();
            if (!ok)
                log.LogDebug("ConditionalOnConfig missing: {Path}", c.Path);
            return ok;
        }

        private static bool EqualsValue(IConfigurationSection s, ConditionalOnConfigAttribute c, ILogger log)
        {
            var raw = s.Value;
            var ok = s.Exists() && raw is not null &&
                     string.Equals(raw.Trim(), c.HavingValue!.Trim(),
                                   c.CaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (!ok)
                log.LogDebug("ConditionalOnConfig mismatch: {Path}", c.Path);
            return ok;
        }

        private static Dictionary<Type, IConfigConverter> DiscoverConverters(IServiceCollection services, ILogger log)
        {
            var map = new Dictionary<Type, IConfigConverter>();
            var temp = services.BuildServiceProvider();

            foreach (var t in TypeDiscovery.FindTypesWithAttribute<ConfigConverterAttribute>(
                         AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName)).ToArray()))
            {
                TryAddConverter(map, temp, t, log);
            }
            if (map.Count > 0)
                log.LogDebug("🔧 Discovered {Count} ConfigConverter(s).", map.Count);
            return map;
        }

        private static void TryAddConverter(Dictionary<Type, IConfigConverter> map, IServiceProvider sp, Type t, ILogger log)
        {
            var attr = t.GetCustomAttribute<ConfigConverterAttribute>(false)!;
            var inst = CreateConverter(sp, t);
            if (inst is null)
            {
                log.LogWarning("⚠️ Failed to create converter {Type} for {Target}.", t.FullName, attr.TargetType.Name);
                return;
            }
            map[attr.TargetType] = inst;
        }

        private static IConfigConverter? CreateConverter(IServiceProvider sp, Type t)
        {
            try
            { return (IConfigConverter?)ActivatorUtilities.CreateInstance(sp, t); }
            catch
            {
                try
                { return (IConfigConverter?)Activator.CreateInstance(t); }
                catch { return null; }
            }
        }

        // ------------------- Config conversion pipeline ------------------

        public static object? ResolveFromConfig(IConfiguration cfg, Type target, AppConfigValueAttribute a, ILogger _)
        {
            var s = cfg.GetSection(a.Path);
            if (s.Exists())
                return BindOrConvert(s, target);

            if (a.Default is not null)
                return DefaultTo(target, a.Default);

            return Nullable.GetUnderlyingType(target) is not null || !target.IsValueType
                ? null
                : throw new InvalidOperationException($"Missing configuration for '{a.Path}' (type {target.Name}).");
        }

        private static object? BindOrConvert(IConfigurationSection s, Type target)
        {
            if (!IsSimple(target))
                return BindSection(s, target);
            var raw = s.Value;
            return raw is null ? null : ConvertTo(raw, target);
        }

        private static object BindSection(IConfigurationSection s, Type target)
        {
            var obj = Activator.CreateInstance(target)!;
            s.Bind(obj);
            return obj;
        }

        private static object? DefaultTo(Type target, string raw)
        {
            if (!IsSimple(target))
            {
                var c = TryConverter(raw, target);
                if (c.success)
                    return c.value;

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
            var c = TryConverter(raw, t);
            if (c.success)
                return c.value;

            if (t.IsEnum)
                return Enum.Parse(t, raw, true);
            if (t == typeof(Guid))
                return Guid.Parse(raw);
            if (t == typeof(TimeSpan))
                return TimeSpan.Parse(raw, CultureInfo.InvariantCulture);
            if (t == typeof(DateTime))
                return DateTime.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (t == typeof(DateTimeOffset))
                return DateTimeOffset.Parse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (t == typeof(string))
                return raw;

            return ParseNumericOrChangeType(raw, t);
        }

        private static object ParseNumericOrChangeType(string raw, Type t)
        {
            if (t == typeof(float))
                return float.Parse(Norm(raw), NumberStyles.Float | NumberStyles.AllowThousands, CultureBox);
            if (t == typeof(double))
                return double.Parse(Norm(raw), NumberStyles.Float | NumberStyles.AllowThousands, CultureBox);
            if (t == typeof(decimal))
                return decimal.Parse(Norm(raw), NumberStyles.Number, CultureBox);
            if (t == typeof(byte))
                return byte.Parse(Norm(raw, false), NumberStyles.Integer, CultureBox);
            if (t == typeof(sbyte))
                return sbyte.Parse(Norm(raw, false), NumberStyles.Integer, CultureBox);
            if (t == typeof(short))
                return short.Parse(Norm(raw, false), NumberStyles.Integer, CultureBox);
            if (t == typeof(ushort))
                return ushort.Parse(Norm(raw, false), NumberStyles.Integer, CultureBox);
            if (t == typeof(int))
                return int.Parse(Norm(raw, false), NumberStyles.Integer, CultureBox);
            if (t == typeof(uint))
                return uint.Parse(Norm(raw), NumberStyles.Integer, CultureBox);
            if (t == typeof(long))
                return long.Parse(Norm(raw), NumberStyles.Integer, CultureBox);
            if (t == typeof(ulong))
                return ulong.Parse(Norm(raw), NumberStyles.Integer, CultureBox);

            return System.Convert.ChangeType(raw, t, CultureBox)!;
        }

        private static readonly CultureInfo CultureBox = CultureInfo.InvariantCulture;

        private static string Norm(string s, bool allowUL = true)
        {
            s = s.Trim().Replace("_", "");
            if (s.Length == 0)
                return s;

            var last = char.ToLowerInvariant(s[^1]);
            if (last is 'f' or 'd' or 'm')
                return s[..^1];
            if (!allowUL)
                return s;

            int end = s.Length;
            while (end > 0 && "ul".Contains(char.ToLowerInvariant(s[end - 1])))
                end--;
            return end != s.Length ? s[..end] : s;
        }

        private static (bool success, object? value) TryConverter(string raw, Type t)
        {
            var dict = _converters;
            if (dict is null)
                return (false, null);
            return dict.TryGetValue(t, out var conv) ? (true, conv.Convert(raw)) : (false, null);
        }
    }
}
