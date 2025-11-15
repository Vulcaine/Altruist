/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Altruist;

/// <summary>
/// Builds (and caches) a constructor-based dependency graph for a given implementation
/// type, then ensures all dependencies are registered in DI **before** registering
/// the requested type. Handles collections/arrays, conditional registrations, and
/// framework-provided abstractions. Detects cycles and gives a readable error.
///
/// It can delegate some registrations to provider packages via IServiceFactory
/// (e.g. IVault&lt;T&gt; construction in Postgres package).
/// </summary>
public static class DependencyPlanner
{
    private sealed record Node(Type Impl, HashSet<Type> DirectDeps);

    private static readonly ConcurrentDictionary<Type, Node> _graphCache = new();

    private static readonly Assembly[] _assemblies = AppDomain.CurrentDomain
        .GetAssemblies()
        .Where(a => !a.IsDynamic && !string.IsNullOrWhiteSpace(a.FullName))
        .ToArray();

    // Known framework-provided abstractions we should not try to register
    private static readonly HashSet<Type> _frameworkProvided = new()
    {
        typeof(ILoggerFactory),
        typeof(IServiceProvider),
        typeof(IServiceCollection),
        typeof(IConfiguration),
        typeof(IHostApplicationLifetime)
    };

    /// <summary>
    /// Ensure that <paramref name="implType"/> and all of its transitive constructor
    /// dependencies are registered in <paramref name="services"/> (dependencies first).
    /// Honors ConditionalOnConfig via DependencyResolver.ShouldRegister.
    /// </summary>
    public static void EnsureDependenciesRegistered(
        IServiceCollection services,
        IConfiguration cfg,
        ILogger log,
        Type implType)
    {
        var visiting = new Stack<Type>();
        var visited = new HashSet<Type>();

        // Build graph (cached) and then register bottom-up
        BuildGraph(implType, cfg, log);
        RegisterBottomUp(implType, services, cfg, log, visiting, visited);
    }

    // ---------------- graph build ----------------

    private static Node BuildGraph(Type impl, IConfiguration cfg, ILogger log)
    {
        return _graphCache.GetOrAdd(impl, t =>
        {
            var ctor = SelectCtor(t);
            var deps = new HashSet<Type>();

            foreach (var p in ctor.GetParameters())
            {
                // Skip config-bound & optional params
                if (p.GetCustomAttribute<AppConfigValueAttribute>(false) is not null)
                    continue;
                if (p.HasDefaultValue)
                    continue;

                // Ignore non-serviceable types (string, primitives, enums, pointers, delegates, etc.)
                if (IsNonServiceable(p.ParameterType))
                    continue;

                // Expand collection/array to its element abstraction
                foreach (var abs in ExpandParameterToAbstractions(p.ParameterType))
                {
                    if (IsNonServiceable(abs))
                        continue;      // e.g., string[], int[], IEnumerable<string>
                    if (IsSkippable(abs))
                        continue;      // framework-provided, ILogger<T>
                    deps.Add(abs);
                }
            }

            return new Node(t, deps);
        });
    }

    private static ConstructorInfo SelectCtor(Type t) =>
        t.GetConstructors(BindingFlags.Public | BindingFlags.Instance)
         .OrderByDescending(c => c.GetCustomAttribute<ActivatorUtilitiesConstructorAttribute>() is not null)
         .ThenByDescending(c => c.GetParameters().Length)
         .First();

    // ---------------- registration (bottom-up) ----------------

    private static void RegisterBottomUp(
        Type implType,
        IServiceCollection services,
        IConfiguration cfg,
        ILogger log,
        Stack<Type> visiting,
        HashSet<Type> visited)
    {
        if (visited.Contains(implType))
            return;

        if (visiting.Contains(implType))
        {
            var cycle = visiting.Reverse().Concat(new[] { implType }).Select(DependencyResolver.GetCleanName);
            var path = string.Join(" → ", cycle);
            var msg = $"Circular dependency detected: {path}";
            log.LogCritical(msg);
            Environment.Exit(1);
        }

        visiting.Push(implType);
        try
        {
            var node = BuildGraph(implType, cfg, log);

            // For each abstraction dep, ensure at least one implementation is registered
            foreach (var abs in node.DirectDeps)
            {
                if (IsNonServiceable(abs))
                    continue;
                if (IsAlreadyRegistered(services, abs))
                    continue;

                // Special-case generic IVault<T> via provider IServiceFactory.
                if (IsVaultAbstraction(abs))
                {
                    EnsureServiceFactoriesAreAvailable(services, cfg, log);
                    RegisterServiceViaFactories(services, cfg, log, abs);
                    continue;
                }

                var candidates = FindCandidateImplementations(abs, cfg, log);

                if (candidates.Count == 0)
                {
                    // If 'abs' is a concrete class and ShouldRegister says OK, we can register it as-is.
                    if (abs.IsClass && !abs.IsAbstract && DependencyResolver.ShouldRegister(abs, cfg, log))
                    {
                        RegisterBottomUp(abs, services, cfg, log, visiting, visited);
                        RegisterOne(services, cfg, log, abs, abs, ServiceLifetime.Singleton);
                    }

                    // otherwise, leave it unresolved; the runtime resolver will produce a helpful error.
                    continue;
                }

                // Pick the first candidate (use [Service] to disambiguate in apps)
                var chosen = candidates[0];

                // Ensure dependencies of candidate are registered first
                RegisterBottomUp(chosen.impl, services, cfg, log, visiting, visited);

                // Finally register the chosen implementation for the abstraction
                RegisterOne(services, cfg, log, chosen.impl, abs, chosen.lifetime);
            }

            // Ensure 'implType' itself is registered (self mapping), if not present
            if (!IsAlreadyRegistered(services, implType) && !IsNonServiceable(implType))
            {
                RegisterOne(services, cfg, log, implType, implType, ServiceLifetime.Singleton);
            }

            visited.Add(implType);
        }
        finally
        {
            visiting.Pop();
        }
    }

    private static bool IsAlreadyRegistered(IServiceCollection services, Type serviceType)
        => services.Any(d => d.ServiceType == serviceType);

    private static bool IsSkippable(Type t)
    {
        if (_frameworkProvided.Contains(t))
            return true;

        // Skip generic ILogger<T>
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ILogger<>))
            return true;

        return false;
    }

    /// <summary>
    /// Non-serviceable = things we must not try to wire up via DI:
    /// primitives, enums, string, pointers, byrefs, delegates, simple BCLs, Nullable&lt;T&gt; of simple, etc.
    /// </summary>
    private static bool IsNonServiceable(Type t)
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

    // A local copy; keep in sync with DependencyResolver's notion of "simple"
    private static bool IsSimple(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        return type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal) ||
               type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid);
    }

    // ---------------- vault-specific logic (using generic IServiceFactory) ----------------

    private static bool IsVaultAbstraction(Type t)
        => t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IVault<>);

    /// <summary>
    /// Ensure IServiceFactory implementations are registered, so they can be used to create
    /// provider-specific services (e.g. IVault&lt;T&gt;).
    /// </summary>
    private static void EnsureServiceFactoriesAreAvailable(IServiceCollection services, IConfiguration cfg, ILogger log)
    {
        // If any IServiceFactory is already registered, we're good.
        if (services.Any(d => d.ServiceType == typeof(IServiceFactory)))
            return;

        // Discover implementations that opted-in via [Service]
        var factories = _assemblies
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IServiceFactory).IsAssignableFrom(t))
            .SelectMany(t =>
                t.GetCustomAttributes<ServiceAttribute>()
                 .Where(sa => (sa.ServiceType ?? t) == typeof(IServiceFactory))
                 .Select(sa => (impl: t, lifetime: sa.Lifetime)))
            .Where(x => DependencyResolver.ShouldRegister(x.impl, cfg, log))
            .ToList();

        // Fallback: allow implicit discovery if the impl has no [Service] but is present.
        if (factories.Count == 0)
        {
            factories = _assemblies
                .SelectMany(SafeGetTypes)
                .Where(t => t is { IsClass: true, IsAbstract: false } && typeof(IServiceFactory).IsAssignableFrom(t))
                .Where(t => DependencyResolver.ShouldRegister(t, cfg, log))
                .Select(t => (impl: t, lifetime: ServiceLifetime.Singleton))
                .ToList();
        }

        foreach (var f in factories)
            RegisterOne(services, cfg, log, f.impl, typeof(IServiceFactory), f.lifetime);
    }

    /// <summary>
    /// Register a service (currently used for IVault&lt;T&gt;) so that it is created via IServiceFactory.
    /// </summary>
    private static void RegisterServiceViaFactories(
        IServiceCollection services,
        IConfiguration cfg,
        ILogger log,
        Type serviceType)
    {
        if (IsAlreadyRegistered(services, serviceType))
            return;

        services.Add(new ServiceDescriptor(
            serviceType,
            sp =>
            {
                var factories = sp.GetServices<IServiceFactory>().ToList();
                var factory = factories.FirstOrDefault(f => f.CanCreate(serviceType));
                if (factory is null)
                {
                    var msg =
                        $"❌ No IServiceFactory can create '{DependencyResolver.GetCleanName(serviceType)}'. " +
                        "Did you reference the proper provider package (e.g., Altruist.Postgres) and enable it via config?";
                    log.LogCritical(msg);
                    Environment.Exit(1);
                    throw new InvalidOperationException(msg);
                }

                return factory.Create(sp, serviceType);
            },
            ServiceLifetime.Singleton));

        log.LogDebug("🔧 Planned registration via IServiceFactory: {Service} ({Lifetime})",
            DependencyResolver.GetCleanName(serviceType),
            ServiceLifetime.Singleton);
    }

    // ---------------- candidates ----------------

    private static List<(Type impl, ServiceLifetime lifetime)> FindCandidateImplementations(
    Type abstraction, IConfiguration cfg, ILogger log)
    {
        var explicitImpls = _assemblies
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && abstraction.IsAssignableFrom(t))
            .SelectMany(t =>
                t.GetCustomAttributes<ServiceAttribute>()
                 .Where(sa => (sa.ServiceType ?? t) == abstraction)
                 .Select(sa => (impl: t, lifetime: sa.Lifetime)))
            .Where(x => DependencyResolver.ShouldRegister(x.impl, cfg, log))
            .Distinct()
            .ToList();

        return explicitImpls;
    }

    private static IEnumerable<Type> SafeGetTypes(Assembly a)
    {
        try
        { return a.GetTypes(); }
        catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t is not null)!; }
    }

    private static void RegisterOne(
        IServiceCollection services,
        IConfiguration cfg,
        ILogger log,
        Type implType,
        Type serviceType,
        ServiceLifetime lifetime)
    {
        // Avoid duplicates; also avoid ever registering non-serviceable/framework types
        if (IsAlreadyRegistered(services, serviceType))
            return;
        if (IsNonServiceable(serviceType) || IsSkippable(serviceType))
            return;

        services.Add(new ServiceDescriptor(
            serviceType,
            sp =>
            {
                var obj = DependencyResolver.CreateWithConfiguration(sp, cfg, implType, log, lifetime);
                _ = DependencyResolver.InvokePostConstructAsync(obj, sp, cfg, log);
                return obj!;
            },
            lifetime));

        log.LogDebug("🔧 Planned registration: {Service} → {Impl} ({Lifetime})",
            DependencyResolver.GetCleanName(serviceType),
            DependencyResolver.GetCleanName(implType),
            lifetime);
    }

    // ---------------- parameter → abstractions (expansion) ----------------

    private static IEnumerable<Type> ExpandParameterToAbstractions(Type t)
    {
        // Arrays → element type
        if (t.IsArray)
        {
            var e = t.GetElementType()!;
            if (!IsNonServiceable(e))
                yield return e;
            yield break;
        }

        if (t.IsGenericType)
        {
            var def = t.GetGenericTypeDefinition();

            // supported collections: IEnumerable<T>, IList<T>, ICollection<T>, IReadOnlyList<T>, List<T>, HashSet<T>
            if (def == typeof(IEnumerable<>) ||
                def == typeof(IList<>) ||
                def == typeof(ICollection<>) ||
                def == typeof(IReadOnlyList<>) ||
                def == typeof(List<>) ||
                def == typeof(HashSet<>))
            {
                var e = t.GetGenericArguments()[0];
                if (!IsNonServiceable(e))
                    yield return e;
                yield break;
            }
        }

        // regular single dependency
        yield return t;
    }
}
