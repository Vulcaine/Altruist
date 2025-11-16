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

        private static readonly MethodInfo? _genericGetKeyedService =
            typeof(ServiceProviderServiceExtensions)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "GetKeyedService" &&
                    m.IsGenericMethodDefinition &&
                    m.GetParameters().Length == 2);

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
                // 1) If already built, just return it.
                if (_singletonCache.TryGetValue(impl, out var existing))
                    return existing;

                // 2) Build the instance (this is where circular detection happens).
                var obj = CreateInstanceInternal(sp, cfg, impl, log);

                // 3) Cache it for future calls.
                _singletonCache[impl] = obj;

                return obj;
            }

            // Transient / other lifetimes
            return CreateInstanceInternal(sp, cfg, impl, log);
        }

        private static object CreateInstanceInternal(IServiceProvider sp, IConfiguration cfg, Type impl, ILogger log)
        {
            var path = GetConstructionStack();

            if (path.Contains(impl))
            {
                // 1) First, try to get an already-constructed instance from the provider.
                //    This covers cases where the same implementation type was registered
                //    under a different service type and is already built.
                var fromProvider = sp.GetService(impl);
                if (fromProvider is not null)
                    return fromProvider;

                // 2) Then, fall back to our own singleton cache.
                //    If we've already finished constructing this impl, use it.
                if (_singletonCache.TryGetValue(impl, out var cached))
                    return cached;

                // 3) At this point, we are genuinely in a construction cycle where
                //    no concrete instance exists yet → fatal circular dependency.
                var cycle = FormatCyclePath(path, impl);
                var msg = $"Circular dependency detected while creating {GetCleanName(impl)}. Path: {cycle}";

                try
                {
                    var lf = sp.GetService<ILoggerFactory>();
                    var providerLogger = lf?.CreateLogger(impl) ?? log;
                    FailAndExit(providerLogger, msg);
                }
                catch
                {
                    FailAndExit(log, msg);
                }

                throw new InvalidOperationException(msg);
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

            var gatingConds = conds.Where(c => string.IsNullOrEmpty(c.KeyField)).ToArray();

            return gatingConds.Length == 0 || gatingConds.All(c => ConditionOk(c, cfg, log));
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
        ///  - it MUST return void, Task, or ValueTask,
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

        // ---------------------- Shared helpers (for Planner etc.) -------------------------

        /// <summary>
        /// Select the preferred constructor for a type:
        ///  - prefer the one marked with [ActivatorUtilitiesConstructor] if present,
        ///  - otherwise the widest (most parameters) public ctor.
        /// </summary>
        internal static ConstructorInfo SelectCtor(Type t) =>
            t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
             .OrderByDescending(c => c.GetCustomAttribute<ActivatorUtilitiesConstructorAttribute>() is not null)
             .ThenByDescending(c => c.GetParameters().Length)
             .First();

        /// <summary>
        /// "Simple" types that must be provided by config or default values, not by DI.
        /// Mirrors the logic used for config conversion and non-serviceability.
        /// </summary>
        internal static bool IsSimple(Type type)
        {
            type = Nullable.GetUnderlyingType(type) ?? type;
            return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
                   type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid);
        }

        /// <summary>
        /// Non-serviceable = things we must not try to wire up via DI:
        /// primitives, enums, string, pointers, byrefs, delegates, simple BCLs, Nullable&lt;T&gt; of simple, etc.
        /// Used by planner and resolver.
        /// </summary>
        internal static bool IsNonServiceable(Type t)
        {
            // unwrap Nullable<T>
            var nn = Nullable.GetUnderlyingType(t) ?? t;

            if (nn.IsPointer || nn.IsByRef)
                return true;

            // Simple primitives/enums/string/DateTime/etc.
            if (IsSimple(nn))
                return true;

            // Delegates (Func<>, Action<>, custom delegates)
            if (typeof(Delegate).IsAssignableFrom(nn))
                return true;

            // Open generics are not serviceable here
            if (nn.ContainsGenericParameters)
                return true;

            return false;
        }

        /// <summary>
        /// Central helper for planner and service registration:
        /// registers a service so that its instances are created via DependencyResolver
        /// and cached appropriately. The planner is responsible for deciding *what*
        /// gets registered; this method only does the actual DI registration.
        /// </summary>
        internal static void RegisterPlannedService(
            IServiceCollection services,
            IConfiguration cfg,
            ILogger log,
            Type implType,
            Type serviceType,
            ServiceLifetime lifetime)
        {
            // Avoid duplicates; also avoid ever registering non-serviceable types
            if (services.Any(d => d.ServiceType == serviceType))
                return;
            if (IsNonServiceable(serviceType))
                return;

            services.Add(new ServiceDescriptor(
                serviceType,
                sp =>
                {
                    // Construct instance (honoring singleton cache)
                    var obj = CreateWithConfiguration(sp, cfg, implType, log, lifetime);
                    return obj!;
                },
                lifetime));

            log.LogDebug("🔧 Planned registration: {Service} → {Impl} ({Lifetime})",
                GetCleanName(serviceType),
                GetCleanName(implType),
                lifetime);
        }

        private static object? Arg(IServiceProvider sp, IConfiguration cfg, ParameterInfo p, ILogger log)
        {
            var paramType = p.ParameterType;
            var a = p.GetCustomAttribute<AppConfigValueAttribute>(false);
            if (a is not null)
                return ResolveFromConfig(cfg, p.ParameterType, a, log);

            var keyedAttr = p.GetCustomAttribute<ServiceKeyAttribute>(false);
            if (keyedAttr is not null)
            {
                var keyed = TryResolveKeyedService(sp, paramType, keyedAttr.Key, log);
                if (keyed is not null)
                    return keyed;

                var errMsg =
                    $"❌ No keyed service registered for '{GetCleanName(paramType)}' " +
                    $"with key '{keyedAttr.Key}' (parameter '{p.Name}' in '{GetCleanName(p.Member.DeclaringType!)}').";
                FailAndExit(log, errMsg);
                throw new InvalidOperationException(errMsg);
            }

            if (IsSimple(paramType))
            {
                if (p.HasDefaultValue)
                    return p.DefaultValue;

                if (Nullable.GetUnderlyingType(paramType) is not null)
                    return null;

                var owner = GetCleanName(p.Member.DeclaringType!);
                var pn = p.Name ?? "param";
                var tn = GetCleanName(paramType);
                var errMsg =
                    $"❌ Parameter '{pn}' of '{owner}' is a simple type '{tn}' " +
                    "and has no [AppConfigValue] or default value. " +
                    "Bind it from configuration with [AppConfigValue] or give it a default in the constructor.";
                FailAndExit(log, errMsg);
                throw new InvalidOperationException(errMsg);
            }

            // 4) Handle all supported collection kinds (Spring-style)
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
                    var resultCollection = CreateCollectionOf(elemType, genDef, servicesEnumObj!);
                    return resultCollection;
                }
            }

            // 5) Arrays: T[]
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

            // 6) Try resolve the exact service from the provider
            var service = sp.GetService(paramType);
            if (service is not null)
                return service;

            // 7) Optional/default value for non-simple complex types
            if (p.HasDefaultValue)
                return p.DefaultValue;

            // 8) Nullable value types (T?) → default(T?) == null
            if (Nullable.GetUnderlyingType(paramType) is not null)
                return null;

            // 9) Fallback default(T) for structs
            if (paramType.IsValueType)
                return Activator.CreateInstance(paramType);

            // 10) Truly unresolved reference type → analyze & throw smart error
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

        internal static object? TryResolveKeyedService(IServiceProvider sp, Type serviceType, string key, ILogger log)
        {
            if (_genericGetKeyedService is null)
            {
                var msg =
                    "Keyed DI is not available: IServiceProvider.GetKeyedService<T>(object) " +
                    "extension method could not be found.";
                FailAndExit(log, msg);
                throw new InvalidOperationException(msg);
            }

            var gm = _genericGetKeyedService.MakeGenericMethod(serviceType);
            var result = gm.Invoke(null, new object[] { sp, key });
            return result;
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
        internal static void FailAndExit(ILogger log, string message, Exception? ex = null)
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
                    addMethod.Invoke(set, new[] { elemType.IsInstanceOfType(s) ? s : Convert.ChangeType(s, elemType) });

                return set;
            }

            // Default: List<T> (covers IEnumerable, IList, ICollection, IReadOnlyList)
            var listType = typeof(List<>).MakeGenericType(elemType);
            var list = Activator.CreateInstance(listType)!;

            var addMethod2 = listType.GetMethod("Add", new[] { elemType })!;
            foreach (var s in elements)
                addMethod2.Invoke(list, new[] { elemType.IsInstanceOfType(s) ? s : Convert.ChangeType(s, elemType) });

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

        public static object? ResolveFromConfig(IConfiguration cfg, Type target, AppConfigValueAttribute a, ILogger log)
        {
            if (a is null)
                throw new ArgumentNullException(nameof(a));

            var path = a.Path ?? string.Empty;
            IConfigurationSection section;

            // Track wildcard usage for better error messages
            var starIndex = path.IndexOf('*');
            string? afterStar = null;

            // Support wildcard: everything before '*' is ignored, everything after '*'
            // is treated as a path relative to the *current* cfg.
            if (starIndex >= 0)
            {
                // Slice everything after the star (and optional ':')
                afterStar =
                    starIndex + 1 < path.Length
                        ? path[(starIndex + 1)..].TrimStart(':')
                        : string.Empty;

                if (string.IsNullOrEmpty(afterStar))
                {
                    // '*' by itself means "this section"
                    // If cfg is already a section, just treat it as such.
                    section = cfg as IConfigurationSection ?? cfg.GetSection(string.Empty);
                }
                else
                {
                    // Relative to current cfg root
                    section = cfg.GetSection(afterStar);
                }
            }
            else
            {
                // No wildcard – legacy behavior
                section = cfg.GetSection(path);
            }

            // Found the section -> bind/convert
            if (section.Exists())
                return BindOrConvert(section, target);

            // Attribute-level default (string) wins if provided
            if (a.Default is not null)
                return DefaultTo(target, a.Default);

            // ---- Missing config but the target is OPTIONAL (nullable or reference type) ----
            // In this case we *do not* treat it as fatal; we just return null and let the
            // constructor parameter default / nullable semantics handle it.
            if (Nullable.GetUnderlyingType(target) is not null || !target.IsValueType)
                return null;

            // ---- Missing REQUIRED config for a non-nullable value type ----

            var rootPath = (cfg as IConfigurationSection)?.Path ?? "<root>";
            string hint;

            if (starIndex >= 0)
            {
                var rel = string.IsNullOrEmpty(afterStar) ? "<this section>" : afterStar;

                hint =
                    $"Wildcard path '{path}' was resolved relative to configuration root '{rootPath}', " +
                    $"looking for '{rel}', but that section does not exist.\n" +
                    "This usually means one of the following:\n" +
                    "  • The type is being constructed with the WRONG configuration root\n" +
                    "    (for example the global config instead of a per-item section).\n" +
                    "  • The corresponding list-style ConditionalOnConfig(Path=..., KeyField=...)\n" +
                    "    is pointing at the wrong path, so instances are not registered per item.\n" +
                    "  • Or the expected key is missing from the item in your YAML.\n\n" +
                    "If you expected this to bind from a list item (e.g. 'altruist:game:worlds:items:0:index'),\n" +
                    "make sure this type is constructed with that item section as its configuration root\n" +
                    "(for example via [ConditionalOnConfig(\"altruist:game:worlds:items\", KeyField = \"id\")]).";
            }
            else
            {
                hint =
                    $"Configuration section '{path}' does not exist under root '{rootPath}'.\n" +
                    "Check your configuration file and the path used in [AppConfigValue].";
            }

            var msg = $"Missing configuration for '{a.Path}' (type {target.Name}).\n{hint}";
            FailAndExit(log, msg);
            throw new InvalidOperationException(msg);
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
