using System.Linq.Expressions;
using System.Reflection;
using System.Text;

using Altruist.UORM;

namespace Altruist.Persistence;

internal static class DocumentBuilder
{
    public static VaultDocument Build(Type type)
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
        var uniqueKeys = new List<VaultDocument.UniqueKeyDefinition>();
        var foreignKeys = new List<VaultDocument.VaultForeignKeyDefinition>();
        var accessors = new Dictionary<string, Func<object, object?>>(StringComparer.Ordinal);

        var fieldTypes = new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        var nullablePhysical = new List<string>();

        ScanColumns(type, fields, columns, indexes, foreignKeys, accessors, fieldTypes, nullablePhysical);
        ScanUniqueKeys(type, columns, uniqueKeys);

        var doc = new VaultDocument(
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
        doc.RenamedColumns = ScanRenames(type, columns);
        ScanCopyFromAndDeleted(type, columns, doc);

        var tableDeleteAttr = type.GetCustomAttribute<VaultTableDeleteAttribute>(inherit: true);
        if (tableDeleteAttr != null)
        {
            doc.IsTableDeleted = true;
            doc.TableDeleteReason = tableDeleteAttr.Reason;
        }

        var archiveAttr = type.GetCustomAttribute<VaultArchivedAttribute>(inherit: true);
        if (archiveAttr != null)
        {
            doc.IsTableArchived = true;
            doc.ArchiveTableName = archiveAttr.ArchiveTableName;
            doc.ArchiveReason = archiveAttr.Reason;
        }

        return doc;
    }

    private static void ScanColumns(
        Type type,
        List<string> fields,
        Dictionary<string, string> columns,
        List<string> indexes,
        List<VaultDocument.VaultForeignKeyDefinition> foreignKeys,
        Dictionary<string, Func<object, object?>> accessors,
        Dictionary<string, Type> fieldTypes,
        List<string> nullablePhysical)
    {
        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.GetCustomAttribute<VaultIgnoreAttribute>(inherit: true) is not null)
                continue;

            // [VaultColumnDelete] properties are not part of the active schema —
            // they're scanned separately for migration deletion commands
            if (prop.GetCustomAttribute<VaultColumnDeleteAttribute>(inherit: true) is not null)
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
                foreignKeys.Add(new VaultDocument.VaultForeignKeyDefinition(
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

    private static void ScanUniqueKeys(
        Type type,
        Dictionary<string, string> columns,
        List<VaultDocument.UniqueKeyDefinition> uniqueKeys)
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

            uniqueKeys.Add(new VaultDocument.UniqueKeyDefinition(physicalCols));
        }
    }

    private static void ScanCopyFromAndDeleted(Type type, Dictionary<string, string> columns, VaultDocument doc)
    {
        var copyFrom = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var deleted = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            // [VaultColumnCopy] on active columns — target is in the schema
            var copyAttr = prop.GetCustomAttribute<VaultColumnCopyAttribute>(inherit: true);
            if (copyAttr != null && columns.TryGetValue(prop.Name, out var targetPhysical))
            {
                // Source can be a logical name (nameof) or physical name — resolve both
                var source = copyAttr.SourceColumn;
                // Try logical→physical mapping first
                if (columns.TryGetValue(source, out var sourcePhysical))
                    source = sourcePhysical;
                // Otherwise assume it's already a physical name (column removed from model)
                copyFrom[targetPhysical] = source;
            }

            // [VaultColumnDelete] — not in active columns (skipped by ScanColumns),
            // but we need the physical name for the drop operation
            var delAttr = prop.GetCustomAttribute<VaultColumnDeleteAttribute>(inherit: true);
            if (delAttr != null)
            {
                var colAttr = prop.GetCustomAttribute<VaultColumnAttribute>(inherit: true);
                var physical = colAttr != null && !string.IsNullOrWhiteSpace(colAttr.Name)
                    ? colAttr.Name
                    : ToSnakeCase(prop.Name);
                deleted[physical] = delAttr.Reason;
            }
        }

        doc.CopyFromColumns = copyFrom;
        doc.DeletedColumns = deleted;
    }

    private static Dictionary<string, List<string>> ScanRenames(Type type, Dictionary<string, string> columns)
    {
        var renames = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var attrs = prop.GetCustomAttributes<VaultRenamedFromAttribute>(inherit: true).ToArray();
            if (attrs.Length == 0) continue;

            if (columns.TryGetValue(prop.Name, out var newPhysical))
            {
                // Preserve declaration order (oldest→newest) — planner picks first match in DB
                renames[newPhysical] = attrs.Select(a => a.OldColumnName).ToList();
            }
        }

        return renames;
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
