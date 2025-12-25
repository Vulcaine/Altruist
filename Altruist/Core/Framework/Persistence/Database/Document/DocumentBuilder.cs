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

        var header = type.GetCustomAttribute<VaultAttribute>(inherit: true);
        if (header is null)
            throw new InvalidOperationException(
                $"The type {type.FullName} must have a [Vault] (or derived) attribute.");

        var tableName = string.IsNullOrWhiteSpace(header.Name)
            ? ToSnakeCase(type.Name)
            : header.Name;

        var primaryKey = type.GetCustomAttribute<VaultPrimaryKeyAttribute>(inherit: true);
        var sortingBy = type.GetCustomAttribute<VaultSortingByAttribute>(inherit: true);

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

        // NEW: prefab component ref columns
        if (typeof(IPrefabModel).IsAssignableFrom(type))
            AddPrefabComponentRefColumns(type, fields, columns, indexes, foreignKeys, accessors, fieldTypes, nullablePhysical);

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
            // Skip prefab handle properties (not persisted)
            if (prop.GetCustomAttribute<PrefabComponentAttribute>(inherit: true) is not null)
                continue;

            if (prop.GetCustomAttribute<VaultIgnoreAttribute>(inherit: true) is not null)
                continue;

            var colAttr = prop.GetCustomAttribute<VaultColumnAttribute>(inherit: true);
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

            var fkAttr = prop.GetCustomAttribute<VaultForeignKeyAttribute>(inherit: true);
            if (fkAttr is not null)
            {
                foreignKeys.Add(new Document.VaultForeignKeyDefinition(
                    propertyName: logical,
                    columnName: physical,
                    principalType: fkAttr.PrincipalType,
                    principalPropertyName: fkAttr.PrincipalPropertyName,
                    onDelete: fkAttr.OnDelete));
            }

            if (prop.GetCustomAttribute<VaultColumnIndexAttribute>(inherit: true) is not null)
                indexes.Add(physical);
        }
    }

    private static void AddPrefabComponentRefColumns(
        Type prefabType,
        List<string> fields,
        Dictionary<string, string> columns,
        List<string> indexes,
        List<Document.VaultForeignKeyDefinition> foreignKeys,
        Dictionary<string, Func<object, object?>> accessors,
        Dictionary<string, Type> fieldTypes,
        List<string> nullablePhysical)
    {
        PrefabMetadataRegistry.RegisterPrefab(prefabType);
        var metas = PrefabMetadataRegistry.GetComponents(prefabType);
        if (metas.Count == 0)
            return;

        // Map existing FK definitions by dependent physical column to avoid duplicates
        var fkByDependentCol = new HashSet<string>(
            foreignKeys.Select(fk => fk.ColumnName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var meta in metas)
        {
            // If the ref is an explicit persisted CLR property, ScanColumns already added it.
            // We still want to ensure it has FK + index (if not already).
            if (meta.HasExplicitRefProperty)
            {
                if (!columns.TryGetValue(meta.RefLogicalFieldName, out var physical))
                    continue; // should not happen if property has [VaultColumn]

                // Index FK cols by default (Postgres does NOT auto-index FK columns)
                if (!indexes.Contains(physical, StringComparer.OrdinalIgnoreCase))
                    indexes.Add(physical);

                // Ensure FK exists (some explicit properties might only have [VaultColumn])
                if (!fkByDependentCol.Contains(physical))
                {
                    foreignKeys.Add(new Document.VaultForeignKeyDefinition(
                        propertyName: meta.RefLogicalFieldName,
                        columnName: physical,
                        principalType: meta.ComponentType,
                        principalPropertyName: meta.PrincipalKeyPropertyName,
                        onDelete: "CASCADE"));

                    fkByDependentCol.Add(physical);
                }

                continue;
            }

            // Shadow field: add it to Document as persisted column
            var logical = meta.RefLogicalFieldName; // "__CharacterRef"
            var physicalCol = meta.RefColumnName;   // "prefab_character_ref"

            if (columns.Values.Any(v => string.Equals(v, physicalCol, StringComparison.OrdinalIgnoreCase)))
                continue; // avoid collisions

            fields.Add(logical);
            columns[logical] = physicalCol;

            fieldTypes[logical] = typeof(string);
            accessors[logical] = obj =>
            {
                if (obj is not PrefabModel pm)
                    return null;

                return pm.ComponentRefs.TryGetValue(meta.Name, out var id) ? id : null;
            };

            // nullable (prefab might not have that component set)
            nullablePhysical.Add(physicalCol);

            // index by default (important for joins + deletes)
            indexes.Add(physicalCol);

            // FK => principal StorageId (or overridden principal key)
            foreignKeys.Add(new Document.VaultForeignKeyDefinition(
                propertyName: logical,
                columnName: physicalCol,
                principalType: meta.ComponentType,
                principalPropertyName: meta.PrincipalKeyPropertyName,
                onDelete: "CASCADE"));
        }
    }

    private static void ScanUniqueKeys(
        Type type,
        Dictionary<string, string> columns,
        List<Document.UniqueKeyDefinition> uniqueKeys)
    {
        var uniqueKeyAttrs = type.GetCustomAttributes<VaultUniqueKeyAttribute>(inherit: true).ToArray();
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
