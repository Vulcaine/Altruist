using System.Collections.Concurrent;
using System.Reflection;

namespace Altruist.Persistence;

/// <summary>Shape of a prefab component.</summary>
public enum PrefabComponentKind
{
    /// <summary>The root model (<see cref="PrefabComponentRootAttribute"/>); queries are anchored on its table.</summary>
    Root,
    /// <summary>One related model; the foreign key lives on the root.</summary>
    Single,
    /// <summary>Many related models; the foreign key on each dependent points at the root's StorageId.</summary>
    Collection,
    /// <summary>One related model owned by the root; its foreign key points at the root's StorageId (one row per root).</summary>
    Owned
}

/// <summary>Resolved metadata of one prefab component property, produced by <see cref="PrefabDocument"/>.</summary>
public sealed record PrefabComponentMeta
{
    /// <summary>Component name (the prefab property name).</summary>
    public string Name { get; init; } = default!;
    /// <summary>Component shape.</summary>
    public PrefabComponentKind Kind { get; init; }
    /// <summary>Vault model type of the component (element type for collections).</summary>
    public Type ComponentType { get; init; } = default!;
    /// <summary>The prefab property holding the component.</summary>
    public PropertyInfo Property { get; init; } = default!;

    /// <summary>
    /// No nesting: principal is always root property name.
    /// </summary>
    public string PrincipalPropertyName { get; init; } = default!;

    /// <summary>
    /// FK property name:
    /// - Collection, Owned: FK on dependent model referencing root StorageId
    /// - Single: FK on root model referencing dependent PK
    /// </summary>
    public string ForeignKeyPropertyName { get; init; } = default!;

    /// <summary>
    /// Single-only: dependent PK property name (defaults to StorageId).
    /// </summary>
    public string PrincipalKeyPropertyName { get; init; } = nameof(IVaultModel.StorageId);
}

/// <summary>Resolved structure of a prefab type: its root and all components, keyed by property name.</summary>
public sealed record PrefabMeta
{
    /// <summary>The prefab type.</summary>
    public Type PrefabType { get; init; } = default!;
    /// <summary>Name of the root property.</summary>
    public string RootPropertyName { get; init; } = default!;
    /// <summary>Vault model type of the root.</summary>
    public Type RootComponentType { get; init; } = default!;
    /// <summary>All components including the root, keyed by property name (ordinal).</summary>
    public IReadOnlyDictionary<string, PrefabComponentMeta> ComponentsByName { get; init; } = default!;
}

/// <summary>
/// Builds + caches PrefabMeta from attributes on a prefab type.
/// This is the SINGLE source of truth for prefab structure.
/// </summary>
/// <remarks>
/// Used by <see cref="IPrefabs"/> implementations; application code only needs it to validate a prefab
/// declaration early (e.g. call <see cref="Get{TPrefab}"/> in a test). Thread-safe; results are cached per type.
/// </remarks>
public static class PrefabDocument
{
    private static readonly ConcurrentDictionary<Type, PrefabMeta> _cache = new();

    /// <summary>Returns the (cached) structure of <typeparamref name="TPrefab"/>, building and validating it on first use.</summary>
    /// <typeparam name="TPrefab">The prefab type.</typeparam>
    /// <returns>The prefab metadata.</returns>
    /// <exception cref="InvalidOperationException">The prefab declaration is invalid (see <see cref="Get(Type)"/>).</exception>
    public static PrefabMeta Get<TPrefab>() where TPrefab : PrefabModel
        => Get(typeof(TPrefab));

    /// <summary>Returns the (cached) structure of a prefab type, building and validating it on first use.</summary>
    /// <param name="prefabType">A <see cref="PrefabModel"/> subclass.</param>
    /// <returns>The prefab metadata.</returns>
    /// <exception cref="InvalidOperationException">
    /// The type does not derive from <see cref="PrefabModel"/>; it has zero or several <see cref="PrefabComponentRootAttribute"/>
    /// properties; the root is not a settable <see cref="IVaultModel"/>; a ref's principal is not the root; a ref has no
    /// foreign key; a component property is not settable or not an <see cref="IVaultModel"/> / list of them; an owned
    /// component is a list; or a property is marked both ref and owned.
    /// </exception>
    public static PrefabMeta Get(Type prefabType)
        => _cache.GetOrAdd(prefabType, Build);

    private static PrefabMeta Build(Type prefabType)
    {
        if (!typeof(PrefabModel).IsAssignableFrom(prefabType))
            throw new InvalidOperationException($"{prefabType.Name} must derive from PrefabModel.");

        var props = prefabType.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // 1) Root
        var rootProps = props.Where(p => p.GetCustomAttribute<PrefabComponentRootAttribute>() != null).ToList();

        if (rootProps.Count == 0)
            throw new InvalidOperationException($"{prefabType.Name} must have exactly one [PrefabComponentRoot] property.");

        if (rootProps.Count > 1)
            throw new InvalidOperationException($"{prefabType.Name} has multiple [PrefabComponentRoot] properties. Only one root is allowed.");

        var rootProp = rootProps[0];

        if (!typeof(IVaultModel).IsAssignableFrom(rootProp.PropertyType))
            throw new InvalidOperationException($"{prefabType.Name}.{rootProp.Name} root must be an IVaultModel (single).");

        if (!rootProp.CanWrite)
            throw new InvalidOperationException($"{prefabType.Name}.{rootProp.Name} root property must be settable.");

        var rootName = rootProp.Name;
        var rootType = rootProp.PropertyType;

        var components = new Dictionary<string, PrefabComponentMeta>(StringComparer.Ordinal)
        {
            [rootName] = new PrefabComponentMeta
            {
                Name = rootName,
                Kind = PrefabComponentKind.Root,
                ComponentType = rootType,
                Property = rootProp,
                PrincipalPropertyName = rootName,
                ForeignKeyPropertyName = "",
                PrincipalKeyPropertyName = nameof(IVaultModel.StorageId)
            }
        };

        // 2) Refs
        foreach (var p in props)
        {
            var refAttr = p.GetCustomAttribute<PrefabComponentRefAttribute>();
            var ownedAttr = p.GetCustomAttribute<PrefabComponentOwnedAttribute>();
            if (refAttr is not null && ownedAttr is not null)
                throw new InvalidOperationException($"{prefabType.Name}.{p.Name} cannot be both [PrefabComponentRef] and [PrefabComponentOwned].");

            if (ownedAttr is not null)
            {
                components[p.Name] = BuildOwned(prefabType, p, ownedAttr, rootName);
                continue;
            }

            if (refAttr is null)
                continue;

            if (string.Equals(p.Name, rootName, StringComparison.Ordinal))
                throw new InvalidOperationException($"{prefabType.Name}.{p.Name} cannot be both root and ref.");

            if (!string.Equals(refAttr.Principal, rootName, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"{prefabType.Name}.{p.Name} has Principal='{refAttr.Principal}'. " +
                    $"Principal must be the root property '{rootName}'.");
            }

            if (!p.CanWrite)
                throw new InvalidOperationException($"{prefabType.Name}.{p.Name} component property must be settable.");

            var (kind, componentType) = ResolveKindAndType(p.PropertyType);

            if (kind == PrefabComponentKind.Collection)
            {
                if (string.IsNullOrWhiteSpace(refAttr.ForeignKey))
                    throw new InvalidOperationException($"{prefabType.Name}.{p.Name} collection ref requires ForeignKey.");

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
                if (string.IsNullOrWhiteSpace(refAttr.ForeignKey))
                    throw new InvalidOperationException($"{prefabType.Name}.{p.Name} single ref requires ForeignKey (FK on root).");

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

        return new PrefabMeta
        {
            PrefabType = prefabType,
            RootPropertyName = rootName,
            RootComponentType = rootType,
            ComponentsByName = components
        };
    }

    private static PrefabComponentMeta BuildOwned(Type prefabType, PropertyInfo p, PrefabComponentOwnedAttribute attr, string rootName)
    {
        if (string.Equals(p.Name, rootName, StringComparison.Ordinal))
            throw new InvalidOperationException($"{prefabType.Name}.{p.Name} cannot be both root and owned.");

        if (!string.Equals(attr.Principal, rootName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"{prefabType.Name}.{p.Name} has Principal='{attr.Principal}'. " +
                $"Principal must be the root property '{rootName}'.");
        }

        if (!p.CanWrite)
            throw new InvalidOperationException($"{prefabType.Name}.{p.Name} component property must be settable.");

        if (!typeof(IVaultModel).IsAssignableFrom(p.PropertyType))
            throw new InvalidOperationException($"{prefabType.Name}.{p.Name} owned component must be a single IVaultModel; use [PrefabComponentRef] for a list.");

        if (string.IsNullOrWhiteSpace(attr.ForeignKey))
            throw new InvalidOperationException($"{prefabType.Name}.{p.Name} owned component requires ForeignKey (FK on the owned model).");

        return new PrefabComponentMeta
        {
            Name = p.Name,
            Kind = PrefabComponentKind.Owned,
            ComponentType = p.PropertyType,
            Property = p,
            PrincipalPropertyName = rootName,
            ForeignKeyPropertyName = attr.ForeignKey.Trim(),
            PrincipalKeyPropertyName = nameof(IVaultModel.StorageId)
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
