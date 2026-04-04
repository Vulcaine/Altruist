/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at
http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Altruist.Persistence;

public readonly record struct TransactionalMetadata(
    MethodInfo Method,
    TransactionalAttribute Attribute,
    Type DeclaringType,
    Type? ServiceType);

/// <summary>
/// Global registry for [Transactional] methods.
/// Populated once at startup by scanning assemblies.
/// All operations are O(1) dictionary lookups.
/// </summary>
public static class TransactionalRegistry
{
    private static readonly ConcurrentDictionary<MethodInfo, TransactionalMetadata> _byMethod =
        new ConcurrentDictionary<MethodInfo, TransactionalMetadata>(ReferenceEqualityComparer.Instance);

    private static readonly ConcurrentDictionary<Type, ImmutableArray<TransactionalMetadata>> _byDeclaringType =
        new();

    private static readonly object _initLock = new();

    /// <summary>
    /// Registers a single method with its metadata.
    /// Idempotent and thread-safe. O(1).
    /// </summary>
    public static void Register(MethodInfo method, TransactionalAttribute attribute, Type? serviceType = null)
    {
        if (method is null)
            throw new ArgumentNullException(nameof(method));
        if (attribute is null)
            throw new ArgumentNullException(nameof(attribute));

        var declaringType = method.DeclaringType ?? serviceType ?? throw new InvalidOperationException(
            "Transactional method must have a declaring type or an explicit service type.");

        var meta = new TransactionalMetadata(method, attribute, declaringType, serviceType);

        _byMethod.AddOrUpdate(method, meta, (_, __) => meta);

        _byDeclaringType.AddOrUpdate(
            declaringType,
            _ => ImmutableArray.Create(meta),
            (_, existing) =>
            {
                if (existing.Any(m => m.Method == method))
                    return existing;
                return existing.Add(meta);
            });
    }

    /// <summary>
    /// Registers all [Transactional] methods from given assemblies.
    /// Safe to call multiple times; will re-scan but remain consistent.
    /// </summary>
    public static void WarmUp(params Assembly[] assemblies)
    {
        if (assemblies is null || assemblies.Length == 0)
            return;

        lock (_initLock)
        {
            // We allow calling multiple times with different assembly sets;
            // no need to early-return if _initialized is true.
            foreach (var asm in assemblies)
            {
                if (asm.IsDynamic)
                    continue;

                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
                }

                foreach (var type in types)
                {
                    if (type.IsAbstract || type.IsInterface)
                        continue;

                    var methods = type.GetMethods(
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

                    foreach (var method in methods)
                    {
                        var attr = method.GetCustomAttribute<TransactionalAttribute>(inherit: true);
                        if (attr is null)
                            continue;

                        Register(method, attr, type);
                    }
                }
            }
        }
    }

    /// <summary>
    /// O(1). Returns true if the method is transactional and outputs metadata.
    /// </summary>
    public static bool TryGet(MethodInfo method, out TransactionalMetadata metadata)
    {
        if (method is null)
        {
            metadata = default;
            return false;
        }

        return _byMethod.TryGetValue(method, out metadata);
    }

    /// <summary>
    /// Gets all transactional methods declared on the given type (if any).
    /// O(1) for the type lookup, O(n) to enumerate the methods.
    /// </summary>
    public static ImmutableArray<TransactionalMetadata> GetByDeclaringType(Type type)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type));
        return _byDeclaringType.TryGetValue(type, out var list) ? list : ImmutableArray<TransactionalMetadata>.Empty;
    }

    /// <summary>
    /// Utility to check quickly if a type has at least one [Transactional] method.
    /// O(1) after WarmUp.
    /// </summary>
    public static bool HasTransactionalMethods(Type type)
        => !GetByDeclaringType(type).IsDefaultOrEmpty;

    /// <summary>
    /// For diagnostic / debugging.
    /// </summary>
    public static IReadOnlyCollection<TransactionalMetadata> GetAll()
        => _byMethod.Values.ToArray();
}

/// <summary>
/// Reference equality for reflection types / methods.
/// </summary>
internal sealed class ReferenceEqualityComparer : IEqualityComparer<object>
{
    public static readonly ReferenceEqualityComparer Instance = new();

    private ReferenceEqualityComparer() { }

    public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);

    public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
}
