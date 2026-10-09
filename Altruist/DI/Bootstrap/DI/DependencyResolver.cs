/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Globalization;
using System.Reflection;
using System.Text.Json;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist
{
    /// <summary>
    /// Shared dependency creation &amp; config-binding utilities reused by service &amp; prefab registration.
    /// Centralizes: constructor selection, config arg/property binding, custom converters, and conditional registration.
    /// Also supports invoking a single [PostConstruct] public void instance method with resolved parameters.
    /// </summary>
    /// <remarks>
    /// This is the engine behind <see cref="ServiceAttribute"/>, <see cref="BeanAttribute"/> and
    /// <see cref="ServiceConfigurationAttribute"/>; application code rarely calls it directly. Use
    /// <see cref="CreateWithConfiguration(IServiceProvider, IConfiguration, Type, ILogger)"/> when you need to build an
    /// object the Altruist way (attribute-aware constructor injection) without registering it.
    /// Constructor parameter rules: <see cref="AppConfigValueAttribute"/> → config; <see cref="ServiceKeyAttribute"/> → keyed service;
    /// simple types → default value or error; <see cref="Lazy{T}"/> → deferred lookup (breaks cycles);
    /// <c>IEnumerable/IList/ICollection/IReadOnlyList/List/HashSet&lt;T&gt;</c> and arrays → all registrations of <c>T</c>;
    /// otherwise the registered service, then the parameter default, then null / <c>default</c> for nullable and value types;
    /// an unresolvable reference type fails with diagnostics naming disabled <see cref="ConditionalOnConfigAttribute"/> candidates.
    /// </remarks>
    public static class DependencyResolver
    {
        private static readonly MethodInfo? _genericGetKeyedService =
            typeof(ServiceProviderServiceExtensions)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m =>
                    m.Name == "GetKeyedService" &&
                    m.IsGenericMethodDefinition &&
                    m.GetParameters().Length == 2);

        private static readonly object _convLock = new();
        private static Dictionary<Type, IConfigConverter>? _converters;

        // ---- Lazy<T> support ----
        private static readonly MethodInfo s_createLazyMethod =
            typeof(DependencyResolver).GetMethod(nameof(CreateLazy), BindingFlags.NonPublic | BindingFlags.Static)!;

        private static Lazy<T> CreateLazy<T>(IServiceProvider sp) where T : class
            => new Lazy<T>(() => sp.GetService(typeof(T)) as T ?? throw new InvalidOperationException(
                $"Failed to resolve Lazy<{typeof(T).Name}>: service not registered."));

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
        /// <remarks>Discovers <see cref="ConfigConverterAttribute"/> classes and builds them from a temporary provider. Process-wide: later calls (even with another collection) are no-ops.</remarks>
        /// <param name="services">Collection used to resolve converter dependencies.</param>
        /// <param name="cfg">Configuration.</param>
        /// <param name="log">Logger.</param>
        public static void EnsureConverters(IServiceCollection services, IConfiguration cfg, ILogger log)
        {
            if (_converters is not null)
                return;

            lock (_convLock)
            {
                if (_converters is null)
                    _converters = DiscoverConverters(services, cfg, log);
            }
        }

        /// <summary>
        /// Constructs <paramref name="impl"/> with the widest public constructor (or the one marked
        /// <c>[ActivatorUtilitiesConstructor]</c>), resolving parameters from <paramref name="sp"/> and <paramref name="cfg"/>,
        /// then sets public <see cref="AppConfigValueAttribute"/> properties. Does not run <see cref="PostConstructAttribute"/> hooks
        /// and does not cache the instance.
        /// </summary>
        /// <param name="sp">Provider for dependencies.</param>
        /// <param name="cfg">Configuration root (or a list item section for wildcard paths).</param>
        /// <param name="impl">Concrete type to build.</param>
        /// <param name="log">Logger for failures.</param>
        /// <returns>The new instance.</returns>
        /// <exception cref="InvalidOperationException">No public constructor, a construction cycle, or an unresolvable parameter.</exception>
        /// <remarks>
        /// Construction is stateless across calls: the owning <see cref="IServiceProvider"/> tracks the lifetime of what it
        /// creates (it does natively for descriptors registered with <see cref="ServiceLifetime.Singleton"/>), so each
        /// provider, including per-test child containers, gets its own singletons built against its own dependency graph.
        /// </remarks>
        public static object CreateWithConfiguration(IServiceProvider sp, IConfiguration cfg, Type impl, ILogger log)
            => CreateInstanceInternal(sp, cfg, impl, log);

        private static object CreateInstanceInternal(IServiceProvider sp, IConfiguration cfg, Type impl, ILogger log)
        {
            var path = GetConstructionStack();

            if (path.Contains(impl))
            {
                // Genuine construction cycle. (Use Lazy<T> in a constructor parameter to
                // break it explicitly — see CreateLazy support above.)
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
                var ctors = impl.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
                if (ctors.Length == 0)
                    throw new InvalidOperationException($"Type '{impl.FullName}' has no public constructors. Cannot resolve for DI.");
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

        /// <summary>
        /// Return true if the type should be registered given <see cref="ConditionalOnAssemblyAttribute"/> (every named
        /// assembly loadable), ConditionalOnConfig attributes (gate mode, all must match)
        /// and <see cref="ConditionalOnMissingServiceAttribute"/> (no other active <see cref="ServiceAttribute"/> class provides the service).
        /// </summary>
        /// <param name="t">Candidate type.</param>
        /// <param name="cfg">Configuration to evaluate against.</param>
        /// <param name="log">Logger for debug messages about failed conditions.</param>
        public static bool ShouldRegister(Type t, IConfiguration cfg, ILogger log)
        {
            foreach (var asm in t.GetCustomAttributes<ConditionalOnAssemblyAttribute>(false))
            {
                if (!IsAssemblyAvailable(asm.AssemblyName))
                {
                    log.LogDebug("Skipping {Type}: assembly {Assembly} is not available.", GetCleanName(t), asm.AssemblyName);
                    return false;
                }
            }

            var conds = t.GetCustomAttributes<ConditionalOnConfigAttribute>(false).ToArray();

            var gatingConds = conds.Where(c => string.IsNullOrEmpty(c.KeyField)).ToArray();

            if (gatingConds.Length > 0 && !gatingConds.All(c => ConditionOk(c, cfg, log)))
                return false;

            foreach (var missing in t.GetCustomAttributes<ConditionalOnMissingServiceAttribute>(false))
            {
                var other = FindOtherServiceFor(missing.ServiceType, t, cfg, log);
                if (other is not null)
                {
                    log.LogDebug("Skipping {Type}: {Other} registers {Service}.",
                        GetCleanName(t), GetCleanName(other), GetCleanName(missing.ServiceType));
                    return false;
                }
            }

            return true;
        }

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> s_assemblyAvailable =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Whether an assembly with simple name <paramref name="assemblyName"/> is loaded or loadable (referenced by the
        /// app, resolved through its deps). Used by <see cref="ConditionalOnAssemblyAttribute"/>; a positive answer is cached.
        /// </summary>
        /// <param name="assemblyName">Simple assembly name, e.g. <c>Altruist.Dashboard</c>.</param>
        /// <returns>True when the assembly can be used.</returns>
        public static bool IsAssemblyAvailable(string assemblyName)
        {
            if (string.IsNullOrWhiteSpace(assemblyName))
                return false;
            if (s_assemblyAvailable.TryGetValue(assemblyName, out var known) && known)
                return true;

            var found = AppDomain.CurrentDomain.GetAssemblies()
                .Any(a => string.Equals(a.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase));
            if (!found)
            {
                try
                {
                    found = Assembly.Load(new AssemblyName(assemblyName)) is not null;
                }
                catch (Exception ex) when (ex is FileNotFoundException or FileLoadException or BadImageFormatException)
                {
                    found = false;
                }
            }

            // Only cache hits: an assembly may be loaded later (e.g. a plugin), a miss is re-checked.
            if (found)
                s_assemblyAvailable[assemblyName] = true;
            return found;
        }

        private static readonly object s_serviceTypesLock = new();
        private static int s_serviceTypesAssemblyCount = -1;
        private static List<(Type Impl, Type ServiceType)> s_serviceTypes = new();

        /// <summary>
        /// Another class (not <paramref name="self"/>) that declares <c>[Service(typeof(serviceType))]</c>
        /// and would be registered, ignoring classes that are themselves conditional on that service missing.
        /// </summary>
        private static Type? FindOtherServiceFor(Type serviceType, Type self, IConfiguration cfg, ILogger log)
        {
            foreach (var (impl, svc) in ServiceDeclarations())
            {
                if (svc != serviceType || impl == self)
                    continue;
                if (impl.GetCustomAttributes<ConditionalOnMissingServiceAttribute>(false).Any(a => a.ServiceType == serviceType))
                    continue;
                if (ShouldRegister(impl, cfg, log))
                    return impl;
            }
            return null;
        }

        private static List<(Type Impl, Type ServiceType)> ServiceDeclarations()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                .ToArray();
            lock (s_serviceTypesLock)
            {
                if (assemblies.Length == s_serviceTypesAssemblyCount)
                    return s_serviceTypes;

                var list = new List<(Type, Type)>();
                foreach (var impl in TypeDiscovery.FindTypesWithAttribute<ServiceAttribute>(assemblies))
                    foreach (var sa in impl.GetCustomAttributes<ServiceAttribute>(false))
                        list.Add((impl, sa.ServiceType ?? impl));
                s_serviceTypes = list;
                s_serviceTypesAssemblyCount = assemblies.Length;
                return list;
            }
        }

        /// <summary>
        /// Try to infer a default service type for registration. Falls back to the implementation itself.
        /// Throws for open generics.
        /// </summary>
        /// <param name="impl">Implementation type.</param>
        /// <returns>Currently always <paramref name="impl"/> itself.</returns>
        public static Type InferServiceType(Type impl)
        {
            if (impl.IsGenericTypeDefinition)
                throw new InvalidOperationException($"Cannot infer service type for open generic '{impl.FullName}'.");
            return impl;
        }

        /// <summary>Format a readable, generic-aware type name for logs (e.g. <c>List&lt;String&gt;</c>).</summary>
        /// <param name="type">Type to format.</param>
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
        /// <param name="instance">Object whose hook to run.</param>
        /// <param name="sp">Provider for hook parameters.</param>
        /// <param name="cfg">Configuration for <see cref="AppConfigValueAttribute"/> parameters.</param>
        /// <param name="log">Logger; hook exceptions are logged then rethrown (wrapped in <see cref="TargetInvocationException"/>).</param>
        /// <exception cref="InvalidOperationException">The type breaks one of the rules above.</exception>
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
        /// <summary>
        /// Creates <typeparamref name="T"/> with <see cref="ActivatorUtilities"/> (standard MEDI constructor injection, NOT the
        /// attribute-aware <see cref="CreateWithConfiguration(IServiceProvider, IConfiguration, Type, ILogger)"/>) and then runs its
        /// <see cref="PostConstructAttribute"/> hook. Use for one-off objects that are not registered services.
        /// </summary>
        /// <typeparam name="T">Type to create.</typeparam>
        /// <param name="sp">Provider for constructor and hook dependencies.</param>
        /// <param name="cfg">Configuration for hook parameters.</param>
        /// <param name="log">Logger.</param>
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
        /// <param name="t">Type to inspect.</param>
        /// <exception cref="InvalidOperationException"><paramref name="t"/> has no public constructor.</exception>
        public static ConstructorInfo SelectCtor(Type t)
        {
            var ctors = t.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
            if (ctors.Length == 0)
                throw new InvalidOperationException($"Type '{t.FullName}' has no public constructors. Cannot resolve for DI.");
            return ctors
                .OrderByDescending(c => c.GetCustomAttribute<ActivatorUtilitiesConstructorAttribute>() is not null)
                .ThenByDescending(c => c.GetParameters().Length)
                .First();
        }

        /// <summary>
        /// "Simple" types that must be provided by config or default values, not by DI.
        /// Mirrors the logic used for config conversion and non-serviceability.
        /// </summary>
        /// <param name="type">Type to test (nullable wrappers are unwrapped).</param>
        public static bool IsSimple(Type type)
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
        /// <param name="t">Type to test.</param>
        public static bool IsNonServiceable(Type t)
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
        // Registrations the planner made for a dependency before the dependency's own [Service]
        // attribute was processed (descriptor -> implementation type).
        private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<ServiceDescriptor, Type> s_plannedImplementations = new();

        /// <summary>
        /// The index of a descriptor the dependency planner registered for
        /// <paramref name="serviceType"/> → <paramref name="implType"/>, or -1.
        /// </summary>
        /// <param name="services">Collection to search.</param>
        /// <param name="serviceType">Registered service type.</param>
        /// <param name="implType">Implementation the planner recorded for it.</param>
        public static int IndexOfPlannedRegistration(IServiceCollection services, Type serviceType, Type implType)
        {
            for (var i = 0; i < services.Count; i++)
            {
                var d = services[i];
                if (d.ServiceType == serviceType && !d.IsKeyedService
                    && s_plannedImplementations.TryGetValue(d, out var impl) && impl == implType)
                    return i;
            }
            return -1;
        }

        /// <summary>
        /// Adds a registration of <paramref name="serviceType"/> built from <paramref name="implType"/> via
        /// <see cref="CreateWithConfiguration(IServiceProvider, IConfiguration, Type, ILogger)"/> and records it as
        /// planner-made (see <see cref="IndexOfPlannedRegistration"/>). Non-serviceable types are ignored.
        /// </summary>
        /// <param name="services">Collection to register into.</param>
        /// <param name="cfg">Configuration for construction.</param>
        /// <param name="log">Logger.</param>
        /// <param name="implType">Concrete type to build.</param>
        /// <param name="serviceType">Type to register as.</param>
        /// <param name="lifetime">DI lifetime.</param>
        public static void RegisterPlannedService(
            IServiceCollection services,
            IConfiguration cfg,
            ILogger log,
            Type implType,
            Type serviceType,
            ServiceLifetime lifetime)
        {
            if (IsNonServiceable(serviceType))
                return;

            var descriptor = new ServiceDescriptor(
                serviceType,
                sp =>
                {
                    // Construct instance (honoring singleton cache)
                    var obj = CreateWithConfiguration(sp, cfg, implType, log);
                    return obj!;
                },
                lifetime);
            s_plannedImplementations.AddOrUpdate(descriptor, implType);
            services.Add(descriptor);

            log.LogDebug("🔧 Planned registration: {Service} → {Impl} ({Lifetime})",
                GetCleanName(serviceType),
                GetCleanName(implType),
                lifetime);
        }

        /// <summary>
        /// Resolves one method/constructor parameter with the same rules as constructor injection (see the class remarks).
        /// Useful for invoking attribute-discovered handler methods with injected arguments.
        /// </summary>
        /// <param name="sp">Provider for services.</param>
        /// <param name="cfg">Configuration for <see cref="AppConfigValueAttribute"/>.</param>
        /// <param name="p">The parameter.</param>
        /// <param name="log">Logger.</param>
        /// <returns>The value to pass.</returns>
        /// <exception cref="InvalidOperationException">The parameter cannot be satisfied.</exception>
        public static object? ResolveParameter(IServiceProvider sp, IConfiguration cfg, ParameterInfo p, ILogger log)
            => Arg(sp, cfg, p, log);

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

            // Lazy<T> — deferred resolution (breaks circular deps at construction time)
            if (paramType.IsGenericType && paramType.GetGenericTypeDefinition() == typeof(Lazy<>))
            {
                var innerType = paramType.GetGenericArguments()[0];
                return s_createLazyMethod.MakeGenericMethod(innerType).Invoke(null, [sp])!;
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

        /// <summary>Resolves the keyed registration of <paramref name="serviceType"/> under <paramref name="key"/>, or null.</summary>
        /// <param name="sp">Provider.</param>
        /// <param name="serviceType">Service type.</param>
        /// <param name="key">Service key.</param>
        /// <param name="log">Logger.</param>
        public static object? TryResolveKeyedService(IServiceProvider sp, Type serviceType, string key, ILogger log)
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
            var result = gm.Invoke(null, [sp, key]);
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
        /// Logs a critical dependency resolution failure (also to stdout with a <c>FATAL DI:</c> prefix) and throws
        /// <see cref="InvalidOperationException"/>. It does not exit the process itself; the exception aborts bootstrap.
        /// </summary>
        /// <param name="log">Logger.</param>
        /// <param name="message">Failure message.</param>
        /// <param name="ex">Optional inner exception.</param>
        /// <exception cref="InvalidOperationException">Always.</exception>
        public static void FailAndExit(ILogger log, string message, Exception? ex = null)
        {
            if (ex is not null)
                log.LogCritical(ex, message);
            else
                log.LogCritical(message);

            Console.WriteLine("FATAL DI: " + message);

            throw new InvalidOperationException(message, ex);
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

                var addMethod = setType.GetMethod("Add", [elemType])!;
                foreach (var s in elements)
                    addMethod.Invoke(set, [elemType.IsInstanceOfType(s) ? s : Convert.ChangeType(s, elemType)]);

                return set;
            }

            // Default: List<T> (covers IEnumerable, IList, ICollection, IReadOnlyList)
            var listType = typeof(List<>).MakeGenericType(elemType);
            var list = Activator.CreateInstance(listType)!;

            var addMethod2 = listType.GetMethod("Add", [elemType])!;
            foreach (var s in elements)
                addMethod2.Invoke(list, [elemType.IsInstanceOfType(s) ? s : Convert.ChangeType(s, elemType)]);

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

        private static Dictionary<Type, IConfigConverter> DiscoverConverters(
            IServiceCollection services,
            IConfiguration cfg,
            ILogger log)
        {
            var map = new Dictionary<Type, IConfigConverter>();

            var assemblies = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
                .ToArray();

            var converterTypes = TypeDiscovery
                .FindTypesWithAttribute<ConfigConverterAttribute>(assemblies)
                .ToArray();

            foreach (var t in converterTypes)
            {
                DependencyPlanner.EnsureDependenciesRegistered(services, cfg, log, t);
            }

            using var temp = services.BuildServiceProvider();

            foreach (var t in converterTypes)
            {
                TryAddConverter(map, temp, t, log);
            }

            if (map.Count > 0)
                log.LogDebug("🔧 Discovered {Count} ConfigConverter(s).", map.Count);

            return map;
        }

        private static void TryAddConverter(
            Dictionary<Type, IConfigConverter> map,
            IServiceProvider sp,
            Type t,
            ILogger log)
        {
            var attr = t.GetCustomAttribute<ConfigConverterAttribute>(false)!;
            var inst = CreateConverter(sp, t);
            if (inst is null)
            {
                log.LogWarning("⚠️ Failed to create converter {Type} for {Target}.",
                    t.FullName, attr.TargetType.Name);
                return;
            }

            map[attr.TargetType] = inst;
        }

        private static IConfigConverter? CreateConverter(IServiceProvider sp, Type t)
        {
            try
            {
                return (IConfigConverter?)ActivatorUtilities.CreateInstance(sp, t);
            }
            catch
            {
                try
                {
                    return (IConfigConverter?)Activator.CreateInstance(t);
                }
                catch
                {
                    return null;
                }
            }
        }


        // ------------------- Config conversion pipeline ------------------

        /// <summary>
        /// Reads the value described by <paramref name="a"/> from <paramref name="cfg"/> and converts it to <paramref name="target"/>
        /// (see <see cref="AppConfigValueAttribute"/> for conversion, default and wildcard rules; <see cref="ILiveConfigValue{T}"/>
        /// targets get a <see cref="LiveConfigValue{T}"/>).
        /// </summary>
        /// <param name="cfg">Configuration root or list item section.</param>
        /// <param name="target">Target type.</param>
        /// <param name="a">The attribute with path and default.</param>
        /// <param name="log">Logger.</param>
        /// <returns>The converted value, or null for a missing key of a nullable/reference type.</returns>
        /// <exception cref="InvalidOperationException">The key is missing, has no default, and <paramref name="target"/> is a non-nullable value type.</exception>
        public static object? ResolveFromConfig(IConfiguration cfg, Type target, AppConfigValueAttribute a, ILogger log)
        {
            if (a is null)
                throw new ArgumentNullException(nameof(a));

            var path = a.Path ?? string.Empty;
            IConfigurationSection section;

            // Track wildcard usage for error diagnostics
            var starIndex = path.IndexOf('*');
            string? afterStar = null;

            // -------------------------------------------------------------------
            // ✔ SPECIAL CASE: ILiveConfigValue<T>
            // -------------------------------------------------------------------
            if (target.IsGenericType &&
                target.GetGenericTypeDefinition() == typeof(ILiveConfigValue<>))
            {
                var genArg = target.GetGenericArguments()[0];
                var readKey = ExtractWildcardRelativeKey(a.Path ?? string.Empty);
                var registryKey = ExpandWildcardPath(cfg, a.Path ?? string.Empty);
                var wrapperType = typeof(LiveConfigValue<>).MakeGenericType(genArg);
                var fallback = a.Default is not null
                    ? DefaultTo(genArg, a.Default)
                    : genArg.IsValueType ? Activator.CreateInstance(genArg) : null;
                return Activator.CreateInstance(wrapperType, cfg, readKey, registryKey, fallback);
            }
            // -------------------------------------------------------------------

            // -------------------------------------------------------------------
            // Wildcard ("*") support
            // -------------------------------------------------------------------
            if (starIndex >= 0)
            {
                afterStar = (starIndex + 1 < path.Length)
                    ? path[(starIndex + 1)..].TrimStart(':')
                    : string.Empty;

                if (string.IsNullOrEmpty(afterStar))
                    section = cfg as IConfigurationSection ?? cfg.GetSection(string.Empty);
                else
                    section = cfg.GetSection(afterStar);
            }
            else
            {
                section = cfg.GetSection(path);
            }

            // Found → convert / bind normally
            if (section.Exists())
                return BindOrConvert(section, target);

            // Attribute default → convert to target type
            if (a.Default is not null)
                return DefaultTo(target, a.Default);

            // Target type is nullable OR reference → permit null
            if (Nullable.GetUnderlyingType(target) is not null || !target.IsValueType)
                return null;

            // -------------------------------------------------------------------
            // Missing required config for *non-nullable* value type → fatal
            // F-------------------------------------------------------------------
            var rootPath = (cfg as IConfigurationSection)?.Path ?? "<root>";
            string hint;

            if (starIndex >= 0)
            {
                var rel = string.IsNullOrEmpty(afterStar) ? "<this section>" : afterStar;

                hint =
                    $"Wildcard path '{path}' was resolved relative to configuration root '{rootPath}', " +
                    $"looking for '{rel}', but that section does not exist.\n" +
                    "This usually means:\n" +
                    "  • The type is being constructed with the WRONG configuration root;\n" +
                    "  • Or the conditional registration path is incorrect;\n" +
                    "  • Or the expected key is missing from your config.\n";
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

        private static string ExpandWildcardPath(IConfiguration cfg, string wildcardPath)
        {
            var star = wildcardPath.IndexOf('*');
            if (star < 0)
                return wildcardPath;

            var after = star + 1 < wildcardPath.Length
                ? wildcardPath[(star + 1)..].TrimStart(':')
                : string.Empty;

            // cfg is already the correct per-item root: e.g. "altruist:game:worlds:items:0"
            var root = (cfg as IConfigurationSection)?.Path ?? "";

            if (string.IsNullOrEmpty(after))
                return root;

            return $"{root}:{after}";
        }

        private static string ExtractWildcardRelativeKey(string path)
        {
            var star = path.IndexOf('*');
            if (star < 0)
                return path;

            return (star + 1 < path.Length)
                ? path[(star + 1)..].TrimStart(':')
                : string.Empty;
        }

        private static object? BindOrConvert(IConfigurationSection s, Type target)
        {
            // A scalar value of a complex type is converted by its registered converter (e.g. "red" -> Color);
            // a section with children is bound property by property.
            if (!IsSimple(target) && s.Value is { } scalar)
            {
                var converted = TryConverter(scalar, target);
                if (converted.success)
                    return converted.value;
            }
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
                    var opts = new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true,
                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                    };
                    return JsonSerializer.Deserialize(raw, target, opts);
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
