// DocumentBuilder.cs
/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Linq.Expressions;
using System.Reflection;
using System.Text;

using Altruist.UORM;

namespace Altruist.Persistence;

internal static class DocumentBuilder
{
    public static Document Build(Type type)
    {
        if (type is null)
            throw new ArgumentNullException(nameof(type));

        if (!typeof(IStoredModel).IsAssignableFrom(type))
            throw new InvalidOperationException($"The type {type.FullName} must implement IModel.");

        // Single attribute lookup for EVERYTHING (Vault/Prefab/Derived).
        var header = type.GetCustomAttribute<VaultAttribute>(inherit: false);
        if (header is null)
            throw new InvalidOperationException(
                $"The type {type.FullName} must have a [Vault] (or derived) attribute.");

        var tableName = string.IsNullOrWhiteSpace(header.Name)
            ? ToSnakeCase(type.Name)
            : header.Name;

        var primaryKey = type.GetCustomAttribute<VaultPrimaryKeyAttribute>(inherit: false);
        var sortingBy = type.GetCustomAttribute<VaultSortingByAttribute>(inherit: false);

        var fields = new List<string>();
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        var indexes = new List<string>();
        var uniqueKeys = new List<Document.UniqueKeyDefinition>();
        var foreignKeys = new List<Document.VaultForeignKeyDefinition>();
        var accessors = new Dictionary<string, Func<object, object?>>(StringComparer.Ordinal);

        var fieldTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        var nullablePhysical = new List<string>();

        ScanColumns(type, fields, columns, indexes, foreignKeys, accessors, fieldTypes, nullablePhysical);
        ScanUniqueKeys(type, columns, uniqueKeys);

        var doc = new Document(
            header: header,
            type: type,
            name: tableName,
            fields: fields,
            columns: columns,
            indexes: indexes,
            uniqueKeys: uniqueKeys,
            propertyAccessors: accessors,
            primaryKeyAttribute: primaryKey,
            sortingByAttribute: sortingBy,
            storeHistory: header.StoreHistory,
            foreignKeys: foreignKeys);

        // wire additional metadata after construction (does not affect validation semantics today)
        doc.FieldTypes = fieldTypes;
        doc.NullableColumns = new HashSet<string>(nullablePhysical, StringComparer.OrdinalIgnoreCase);

        return doc;
    }

    private static void ScanColumns(
        Type type,
        List<string> fields,
        Dictionary<string, string> columns,
        List<string> indexes,
        List<Document.VaultForeignKeyDefinition> foreignKeys,
        Dictionary<string, Func<object, object?>> accessors,
        Dictionary<string, Type> fieldTypes,
        List<string> nullablePhysical)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<PrefabComponentAttribute>(inherit: false) is not null)
                continue;

            if (prop.GetCustomAttribute<VaultIgnoreAttribute>(inherit: false) is not null)
                continue;

            var colAttr = prop.GetCustomAttribute<VaultColumnAttribute>(inherit: false);
            if (colAttr is null)
                continue;

            var logical = prop.Name;
            var physical = string.IsNullOrWhiteSpace(colAttr.Name) ? ToSnakeCase(prop.Name) : colAttr.Name!;

            fields.Add(logical);
            columns[logical] = physical;

            fieldTypes[logical] = prop.PropertyType;
            accessors[logical] = CompileAccessor(prop);

            if (colAttr.Nullable)
                nullablePhysical.Add(physical);

            // FK metadata (schema/migrations)
            var fkAttr = prop.GetCustomAttribute<VaultForeignKeyAttribute>(inherit: false);
            if (fkAttr is not null)
            {
                foreignKeys.Add(new Document.VaultForeignKeyDefinition(
                    propertyName: logical,
                    columnName: physical,
                    principalType: fkAttr.PrincipalType,
                    principalPropertyName: fkAttr.PrincipalPropertyName,
                    onDelete: fkAttr.OnDelete));
            }

            // Index metadata
            if (prop.GetCustomAttribute<VaultColumnIndexAttribute>(inherit: false) is not null)
                indexes.Add(physical);
        }
    }

    private static void ScanUniqueKeys(
        Type type,
        Dictionary<string, string> columns,
        List<Document.UniqueKeyDefinition> uniqueKeys)
    {
        var uniqueKeyAttrs = type.GetCustomAttributes<VaultUniqueKeyAttribute>(inherit: false).ToArray();
        if (uniqueKeyAttrs.Length == 0)
            return;

        foreach (var attr in uniqueKeyAttrs)
        {
            if (attr.Keys is null || attr.Keys.Length == 0)
                throw new InvalidOperationException(
                    $"[VaultUniqueKey] on type '{type.FullName}' must specify at least one key.");

            var physicalCols = new List<string>();

            foreach (var key in attr.Keys)
            {
                if (string.IsNullOrWhiteSpace(key))
                    continue;

                // key can be logical property name OR physical column name
                if (columns.TryGetValue(key, out var physicalFromLogical))
                {
                    physicalCols.Add(physicalFromLogical);
                    continue;
                }

                var match = columns.FirstOrDefault(kvp =>
                    string.Equals(kvp.Value, key, StringComparison.OrdinalIgnoreCase));

                if (!string.IsNullOrWhiteSpace(match.Key))
                {
                    physicalCols.Add(match.Value);
                    continue;
                }

                throw new InvalidOperationException(
                    $"[VaultUniqueKey] on '{type.FullName}' references '{key}', " +
                    "but no matching property or column was found.");
            }

            if (physicalCols.Count == 0)
                throw new InvalidOperationException(
                    $"[VaultUniqueKey] on '{type.FullName}' did not resolve any valid columns.");

            uniqueKeys.Add(new Document.UniqueKeyDefinition(physicalCols));
        }
    }

    private static Func<object, object?> CompileAccessor(PropertyInfo property)
    {
        var instance = Expression.Parameter(typeof(object), "instance");
        var castInstance = Expression.Convert(instance, property.DeclaringType!);
        var propertyAccess = Expression.Property(castInstance, property);
        var castResult = Expression.Convert(propertyAccess, typeof(object));
        return Expression.Lambda<Func<object, object?>>(castResult, instance).Compile();
    }

    private static string ToSnakeCase(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var sb = new StringBuilder(value.Length + 4);
        var prevLower = false;

        for (int i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (char.IsUpper(c))
            {
                if (i > 0 && (prevLower || (i + 1 < value.Length && char.IsLower(value[i + 1]))))
                    sb.Append('_');

                sb.Append(char.ToLowerInvariant(c));
                prevLower = false;
            }
            else
            {
                sb.Append(c);
                prevLower = char.IsLetter(c) && char.IsLower(c);
            }
        }

        return sb.ToString();
    }
}
