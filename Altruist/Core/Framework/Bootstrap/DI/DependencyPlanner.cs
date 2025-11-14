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
                // Skip config-bound parameters
                if (p.GetCustomAttribute<AppConfigValueAttribute>(false) is not null)
                    continue;

                // Expand collection/array to its element abstraction
                var abstractions = ExpandParameterToAbstractions(p.ParameterType);

                foreach (var abs in abstractions)
                {
                    if (IsSkippable(abs))
                        continue; // framework-provided, etc.
                    deps.Add(abs);
                    // recursively bake graph for at least one impl, but we don't know impl yet.
                    // We'll resolve concrete impl later during registration.
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
                // If already registered, skip
                if (IsAlreadyRegistered(services, abs))
                    continue;

                // Find candidate impls (prefer those explicitly decorated with [Service])
                var candidates = FindCandidateImplementations(abs, cfg, log);

                if (candidates.Count == 0)
                {
                    // Could be framework provided (and filtered above), but still no impl.
                    // This will fail later in resolver with a good message; we just proceed.
                    continue;
                }

                // Heuristic: pick the first candidate (you could improve with some tie-breakers later)
                var chosen = candidates[0];

                // Ensure dependencies of candidate are registered first
                RegisterBottomUp(chosen.impl, services, cfg, log, visiting, visited);

                // Finally register the chosen implementation for the abstraction
                RegisterOne(services, cfg, log, chosen.impl, abs, chosen.lifetime);
            }

            // Finally ensure 'implType' itself is registered (concrete self mapping),
            // if it's not yet in the container. We don't register an interface mapping here
            // because that is controlled by the caller in AltruistServiceConfig.
            if (!IsAlreadyRegistered(services, implType))
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

    // ---------------- candidates ----------------

    private static List<(Type impl, ServiceLifetime lifetime)> FindCandidateImplementations(
        Type abstraction, IConfiguration cfg, ILogger log)
    {
        // 1) Prefer explicit [Service] mappings whose ServiceType == abstraction
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

        if (explicitImpls.Count > 0)
            return explicitImpls;

        // 2) Otherwise: implicit candidates (any concrete type that implements abstraction)
        var implicitImpls = _assemblies
            .SelectMany(SafeGetTypes)
            .Where(t => t is { IsClass: true, IsAbstract: false } && abstraction.IsAssignableFrom(t))
            .Where(t => DependencyResolver.ShouldRegister(t, cfg, log))
            .Select(t => (impl: t, lifetime: ServiceLifetime.Singleton))
            .Distinct()
            .ToList();

        // If many implicit candidates exist, we take the first; user should disambiguate with [Service].
        return implicitImpls;
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
        // Avoid duplicates
        if (IsAlreadyRegistered(services, serviceType))
            return;

        services.Add(new ServiceDescriptor(
            serviceType,
            sp =>
            {
                var obj = DependencyResolver.CreateWithConfiguration(sp, cfg, implType, log, lifetime);
                // Fire PostConstruct if present (async-friendly)
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
                yield return t.GetGenericArguments()[0];
                yield break;
            }
        }

        // a regular single abstraction
        yield return t;
    }
}
