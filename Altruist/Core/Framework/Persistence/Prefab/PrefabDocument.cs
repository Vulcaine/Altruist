using System.Collections.Concurrent;
using System.Reflection;

namespace Altruist.Persistence;

public enum PrefabComponentKind
{
    Single,
    Collection
}

public sealed record PrefabComponentMeta
{
    public string Name { get; init; } = default!;
    public PrefabComponentKind Kind { get; init; }
    public Type ComponentType { get; init; } = default!;
    public PropertyInfo Property { get; init; } = default!;

    // Principal is always root (no nesting)
    public string PrincipalPropertyName { get; init; } = default!;

    /// <summary>
    /// FK property name:
    /// - Collection: FK on dependent model referencing principal key (usually StorageId)
    /// - Single: FK on principal model referencing dependent PK
    /// </summary>
    public string ForeignKeyPropertyName { get; init; } = default!;

    /// <summary>
    /// Single-only: dependent PK property name (defaults to StorageId)
    /// </summary>
    public string PrincipalKeyPropertyName { get; init; } = nameof(IVaultModel.StorageId);
}

public sealed record PrefabMeta
{
    public Type PrefabType { get; init; } = default!;
    public string RootPropertyName { get; init; } = default!;
    public Type RootComponentType { get; init; } = default!;
    public IReadOnlyDictionary<string, PrefabComponentMeta> ComponentsByName { get; init; } = default!;
}

/// <summary>
/// Builds and caches PrefabMeta from attributes on a prefab type.
/// This is the single source of truth for prefab structure.
/// </summary>
public static class PrefabDocument
{
    private static readonly ConcurrentDictionary<Type, PrefabMeta> _cache = new();

    public static PrefabMeta Get<TPrefab>() where TPrefab : PrefabModel
        => Get(typeof(TPrefab));

    public static PrefabMeta Get(Type prefabType)
        => _cache.GetOrAdd(prefabType, Build);

    private static PrefabMeta Build(Type prefabType)
    {
        if (!typeof(PrefabModel).IsAssignableFrom(prefabType))
            throw new InvalidOperationException($"{prefabType.Name} must derive from PrefabModel.");

        var props = prefabType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // 1) find root
        var rootProps = props
            .Where(p => p.GetCustomAttribute<PrefabComponentRootAttribute>() != null)
            .ToList();

        if (rootProps.Count == 0)
            throw new InvalidOperationException($"{prefabType.Name} must have exactly one [PrefabComponentRoot] property.");

        if (rootProps.Count > 1)
            throw new InvalidOperationException($"{prefabType.Name} has multiple [PrefabComponentRoot] properties. Only one root is allowed.");

        var rootProp = rootProps[0];

        if (!typeof(IVaultModel).IsAssignableFrom(rootProp.PropertyType))
            throw new InvalidOperationException($"{prefabType.Name}.{rootProp.Name} root must be an IVaultModel (single).");

        var rootName = rootProp.Name;
        var rootType = rootProp.PropertyType;

        // 2) gather refs
        var components = new Dictionary<string, PrefabComponentMeta>(StringComparer.Ordinal)
        {
            // Root is also a component
            [rootName] = new PrefabComponentMeta
            {
                Name = rootName,
                Kind = PrefabComponentKind.Single,
                ComponentType = rootType,
                Property = rootProp,
                PrincipalPropertyName = rootName,
                ForeignKeyPropertyName = "", // unused for root
                PrincipalKeyPropertyName = nameof(IVaultModel.StorageId)
            }
        };

        foreach (var p in props)
        {
            var refAttr = p.GetCustomAttribute<PrefabComponentRefAttribute>();
            if (refAttr is null)
                continue;

            if (string.Equals(p.Name, rootName, StringComparison.Ordinal))
                throw new InvalidOperationException($"{prefabType.Name}.{p.Name} cannot be both root and ref.");

            if (!string.Equals(refAttr.Principal, rootName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{prefabType.Name}.{p.Name} has Principal='{refAttr.Principal}', but nesting is disabled. " +
                    $"Principal must be the root property '{rootName}'.");
            }

            var (kind, componentType) = ResolveKindAndType(p.PropertyType);

            if (kind == PrefabComponentKind.Collection)
            {
                // Collection must be some enumerable of IVaultModel
                components[p.Name] = new PrefabComponentMeta
                {
                    Name = p.Name,
                    Kind = PrefabComponentKind.Collection,
                    ComponentType = componentType,
                    Property = p,
                    PrincipalPropertyName = rootName,
                    ForeignKeyPropertyName = refAttr.ForeignKey.Trim(),
                    PrincipalKeyPropertyName = nameof(IVaultModel.StorageId)
                };
            }
            else
            {
                // Single: FK on principal (root) points to dependent PK
                components[p.Name] = new PrefabComponentMeta
                {
                    Name = p.Name,
                    Kind = PrefabComponentKind.Single,
                    ComponentType = componentType,
                    Property = p,
                    PrincipalPropertyName = rootName,
                    ForeignKeyPropertyName = refAttr.ForeignKey.Trim(),
                    PrincipalKeyPropertyName = string.IsNullOrWhiteSpace(refAttr.PrincipalKey)
                        ? nameof(IVaultModel.StorageId)
                        : refAttr.PrincipalKey.Trim()
                };
            }
        }

        // Validate root property is writable (we will set it)
        if (!rootProp.CanWrite)
            throw new InvalidOperationException($"{prefabType.Name}.{rootName} root property must be settable.");

        // Validate ref properties are writable (we will set them on include)
        foreach (var kv in components)
        {
            if (!kv.Value.Property.CanWrite)
                throw new InvalidOperationException($"{prefabType.Name}.{kv.Key} component property must be settable.");
        }

        return new PrefabMeta
        {
            PrefabType = prefabType,
            RootPropertyName = rootName,
            RootComponentType = rootType,
            ComponentsByName = components
        };
    }

    private static (PrefabComponentKind Kind, Type ComponentType) ResolveKindAndType(Type propertyType)
    {
        // Single
        if (typeof(IVaultModel).IsAssignableFrom(propertyType))
            return (PrefabComponentKind.Single, propertyType);

        // Collection: List<T>/IReadOnlyList<T>/IEnumerable<T>
        if (propertyType.IsGenericType)
        {
            var genDef = propertyType.GetGenericTypeDefinition();
            if (genDef == typeof(List<>) ||
                genDef == typeof(IReadOnlyList<>) ||
                genDef == typeof(IEnumerable<>))
            {
                var t = propertyType.GetGenericArguments()[0];
                if (!typeof(IVaultModel).IsAssignableFrom(t))
                    throw new InvalidOperationException($"Collection component element type must be IVaultModel, got {t.Name}.");
                return (PrefabComponentKind.Collection, t);
            }
        }

        throw new InvalidOperationException(
            $"Prefab component property type must be IVaultModel or List/IReadOnlyList/IEnumerable of IVaultModel.");
    }
}
