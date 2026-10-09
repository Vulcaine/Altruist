/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Reflection;

using Altruist.Persistence;
using Altruist.UORM;

using Microsoft.EntityFrameworkCore;

namespace Altruist;

/// <summary>
/// Small reflection helpers for mapping vault model types to storage columns (primary keys, clustering column,
/// mappable properties, column names) honouring <see cref="VaultColumnAttribute"/>, <see cref="VaultIgnoreAttribute"/>
/// and <see cref="VaultPrimaryKeyAttribute"/> across the inheritance chain.
/// </summary>
/// <remarks>
/// Originally written for the ScyllaDB provider (currently disabled); nothing else in the framework calls these
/// today. Column names default to the lower-cased property name, matching CQL conventions. Results are not cached,
/// so cache them yourself if you call these per row.
/// </remarks>
public static class ReflectionUtils
{
    /// <summary>
    /// Returns the type hierarchy from base-most to the given <paramref name="type"/>,
    /// excluding <see cref="object"/>.
    /// </summary>
    /// <param name="type">Type whose hierarchy to list.</param>
    public static IReadOnlyList<Type> GetTypeHierarchy(Type type)
    {
        var stack = new Stack<Type>();
        for (var t = type; t is not null && t != typeof(object); t = t.BaseType)
            stack.Push(t);
        return stack.ToArray();
    }

    private static PropertyInfo? GetPropertyByNameOrColumnName(Type entityType, string name)
    {
        // 1) Try property name (case-insensitive) anywhere in the hierarchy
        var byName = GetPropertyCaseInsensitive(entityType, name);
        if (byName is not null)
            return byName;

        // 2) Try VaultColumnAttribute.Name == name (case-insensitive) across the hierarchy
        foreach (var t in GetTypeHierarchy(entityType))
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var col = p.GetCustomAttribute<VaultColumnAttribute>();
                if (col?.Name is null)
                    continue;
                if (string.Equals(col.Name, name, StringComparison.OrdinalIgnoreCase))
                    return p;
            }
        }

        return null;
    }

    /// <summary>
    /// Collects all <see cref="VaultPrimaryKeyAttribute"/> key names across the inheritance chain
    /// (base → derived), de-duplicates while preserving the first occurrence order, and maps them
    /// to effective column names (respects <see cref="VaultColumnAttribute.Name"/>; otherwise lower-cased property name).
    /// Returns an ordered list of column names suitable for CQL PRIMARY KEY clauses.
    /// Key names may be property names or <see cref="VaultColumnAttribute.Name"/> values (both case-insensitive).
    /// </summary>
    /// <param name="entityType">Vault model type.</param>
    /// <exception cref="InvalidOperationException">A declared key matches no property or column name.</exception>
    public static List<string> GetPrimaryKeyColumns(Type entityType)
    {
        // 1) Gather PK property names across hierarchy (base → derived)
        var pkPropNames = new List<string>();
        foreach (var t in GetTypeHierarchy(entityType))
        {
            var pkAttr = t.GetCustomAttribute<VaultPrimaryKeyAttribute>(inherit: false);
            if (pkAttr?.Keys is null)
                continue;

            foreach (var name in pkAttr.Keys)
            {
                if (!string.IsNullOrWhiteSpace(name) && !pkPropNames.Contains(name))
                    pkPropNames.Add(name);
            }
        }

        // 2) Map property names (or VaultColumn names) → column names
        var keyColumns = new List<string>(pkPropNames.Count);
        foreach (var rawName in pkPropNames)
        {
            var prop = GetPropertyByNameOrColumnName(entityType, rawName)
                ?? throw new InvalidOperationException(
                    $"Primary key '{rawName}' could not be resolved to a property or [VaultColumn(Name=...)] on '{entityType.Name}' or its base types.");

            keyColumns.Add(GetColumnName(prop));
        }

        return keyColumns;
    }

    /// <summary>
    /// Resolves the sorting (clustering) column name for the given <paramref name="document"/> and <paramref name="entityType"/>.
    /// If no sorting is specified, returns <c>null</c>. If specified, returns the actual column name,
    /// honoring <see cref="VaultColumnAttribute.Name"/> or falling back to lower-cased property name.
    /// If the sorting name matches no property, the lower-cased sorting name itself is returned.
    /// </summary>
    /// <param name="document">Vault document describing the model (its <c>SortingBy</c> is used).</param>
    /// <param name="entityType">Vault model type.</param>
    public static string? ResolveSortingColumnName(VaultDocument document, Type entityType)
    {
        var sorting = document.SortingBy;
        if (sorting is null || string.IsNullOrWhiteSpace(sorting.Name))
            return null;

        // Try to resolve the property case-insensitively
        var prop = GetPropertyCaseInsensitive(entityType, sorting.Name);
        if (prop is null)
            return sorting.Name.ToLowerInvariant();

        return GetColumnName(prop);
    }

    /// <summary>
    /// Returns all public instance properties that are not marked with <see cref="VaultIgnoreAttribute"/>.
    /// In case of name collisions across the hierarchy, prefers the most derived declaration.
    /// Indexers are excluded. Names are de-duplicated case-insensitively; properties declared on
    /// <paramref name="entityType"/> come first, then inherited ones, each group ordered by name.
    /// </summary>
    /// <param name="entityType">Vault model type.</param>
    public static IEnumerable<PropertyInfo> GetMappableProperties(Type entityType)
    {
        var map = new Dictionary<string, PropertyInfo>(StringComparer.OrdinalIgnoreCase);

        // Walk base → derived so derived props overwrite
        foreach (var t in GetTypeHierarchy(entityType))
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (p.GetIndexParameters().Length != 0)
                    continue; // skip indexers
                if (p.GetCustomAttribute<VaultIgnoreAttribute>() != null)
                    continue;

                map[p.Name] = p; // most derived wins
            }
        }

        // Preserve declaration order as much as reasonable: return in derived-to-base logical visibility order
        // but stable enough for deterministic SQL generation.
        return map.Values.OrderBy(p => p.DeclaringType == entityType ? 0 : 1)
                         .ThenBy(p => p.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns the CQL column name for a property: uses <see cref="VaultColumnAttribute.Name"/> if present,
    /// otherwise returns the property's name in lower-case.
    /// </summary>
    /// <param name="prop">Mapped property.</param>
    public static string GetColumnName(PropertyInfo prop)
    {
        var columnAttr = prop.GetCustomAttribute<VaultColumnAttribute>();
        return columnAttr?.Name ?? prop.Name.ToLowerInvariant();
    }

    /// <summary>
    /// Finds a public instance property on <paramref name="type"/> (or its base types) by name,
    /// case-insensitively. Returns null when none matches.
    /// </summary>
    /// <param name="type">Type to search (including base types).</param>
    /// <param name="name">Property name.</param>
    public static PropertyInfo? GetPropertyCaseInsensitive(Type type, string name)
    {
        foreach (var t in GetTypeHierarchy(type))
        {
            var p = t.GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);
            if (p != null)
                return p;
        }
        return null;
    }

    /// <summary>
    /// Reads the first attribute of type <typeparamref name="TAttr"/> on <paramref name="type"/> with <c>inherit: false</c>.
    /// Returns null when absent; throws <see cref="AmbiguousMatchException"/> when it is applied more than once.
    /// </summary>
    /// <typeparam name="TAttr">Attribute type.</typeparam>
    /// <param name="type">Type to inspect.</param>
    public static TAttr? GetAttribute<TAttr>(Type type) where TAttr : Attribute =>
        type.GetCustomAttribute<TAttr>(inherit: false);

    /// <summary>
    /// Enumerates all attributes of type <typeparamref name="TAttr"/> on <paramref name="type"/> with <c>inherit: false</c>.
    /// </summary>
    /// <typeparam name="TAttr">Attribute type.</typeparam>
    /// <param name="type">Type to inspect.</param>
    public static IEnumerable<TAttr> GetAttributes<TAttr>(Type type) where TAttr : Attribute =>
        type.GetCustomAttributes<TAttr>(inherit: false) ?? Enumerable.Empty<TAttr>();
}
