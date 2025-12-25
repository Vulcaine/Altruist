// DocumentValidator.cs
/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Reflection;

using Altruist.UORM;

namespace Altruist.Persistence;

internal static class DocumentValidator
{
    public static void Validate(Document doc)
    {
        if (doc is null)
            throw new ArgumentNullException(nameof(doc));

        ValidateStoredModel(doc);
        ValidateHasTypeProperty(doc);

        NormalizeUniqueKeys(doc);
        NormalizeIndexes(doc);

        ValidateForeignKeys(doc);
    }

    private static void ValidateStoredModel(Document doc)
    {
        if (!typeof(IStoredModel).IsAssignableFrom(doc.Type))
            Document.FailAndExit($"The type {doc.Type.FullName} must implement IModel.");
    }

    private static void ValidateHasTypeProperty(Document doc)
    {
        if (doc.Type.GetProperty("Type", BindingFlags.Public | BindingFlags.Instance) is null)
            Document.FailAndExit($"The type {doc.Type.FullName} must have a 'Type' property.");
    }

    private static void NormalizeUniqueKeys(Document doc)
    {
        // De-duplicate unique keys by normalized column-set (case-insensitive, order-insensitive)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<Document.UniqueKeyDefinition>();

        foreach (var uk in doc.UniqueKeys)
        {
            var normalized = NormalizeColumns(uk.Columns);
            if (seen.Add(normalized))
                deduped.Add(uk);
        }

        doc.UniqueKeys.Clear();
        doc.UniqueKeys.AddRange(deduped);
    }

    private static void NormalizeIndexes(Document doc)
    {
        if (doc.Indexes.Count > 1)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            doc.Indexes.RemoveAll(ix => !seen.Add(ix));
        }

        var singleUniqueColumns = new HashSet<string>(
            doc.UniqueKeys.Where(uk => uk.Columns.Count == 1).Select(uk => uk.Columns[0]),
            StringComparer.OrdinalIgnoreCase);

        doc.Indexes.RemoveAll(ix => singleUniqueColumns.Contains(ix));
    }

    private static void ValidateForeignKeys(Document doc)
    {
        if (doc.ForeignKeys is null || doc.ForeignKeys.Count == 0)
            return;

        foreach (var fk in doc.ForeignKeys)
        {
            ValidateOnDelete(doc, fk);
            ValidatePrincipalType(doc, fk);
            ValidatePrincipalPropertyExists(doc, fk);
            ValidateReferencedColumnIsPkOrUnique(doc, fk);
        }
    }

    private static void ValidateOnDelete(Document doc, Document.VaultForeignKeyDefinition fk)
    {
        static bool IsValidOnDelete(string value) =>
            value.Equals("CASCADE", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("NO ACTION", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("RESTRICT", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("SET NULL", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("SET DEFAULT", StringComparison.OrdinalIgnoreCase);

        if (!IsValidOnDelete(fk.OnDelete))
        {
            Document.FailAndExit(
                $"Invalid OnDelete value '{fk.OnDelete}' on foreign key " +
                $"'{doc.Type.FullName}.{fk.PropertyName}'. " +
                "Allowed values are: CASCADE, NO ACTION, RESTRICT, SET NULL, SET DEFAULT.");
        }
    }

    private static void ValidatePrincipalType(Document doc, Document.VaultForeignKeyDefinition fk)
    {
        var principalType = fk.PrincipalType;

        if (!typeof(IStoredModel).IsAssignableFrom(principalType))
        {
            Document.FailAndExit(
                $"Foreign key '{doc.Type.FullName}.{fk.PropertyName}' points to type '{principalType.FullName}' " +
                "which does not implement IStoredModel.");
        }

        // single check covers VaultAttribute + all derived (PrefabAttribute etc.)
        if (principalType.GetCustomAttribute<VaultAttribute>(inherit: false) is null)
        {
            Document.FailAndExit(
                $"Foreign key '{doc.Type.FullName}.{fk.PropertyName}' points to type '{principalType.FullName}' " +
                "which is missing [Vault] (or a derived VaultAttribute).");
        }
    }

    private static void ValidatePrincipalPropertyExists(Document doc, Document.VaultForeignKeyDefinition fk)
    {
        var principalProp = fk.PrincipalType.GetProperty(
            fk.PrincipalPropertyName,
            BindingFlags.Public | BindingFlags.Instance);

        if (principalProp is null)
        {
            Document.FailAndExit(
                $"Foreign key '{doc.Type.FullName}.{fk.PropertyName}' points to " +
                $"'{fk.PrincipalType.FullName}.{fk.PrincipalPropertyName}', " +
                "but that property does not exist.");
        }
    }

    private static void ValidateReferencedColumnIsPkOrUnique(Document doc, Document.VaultForeignKeyDefinition fk)
    {
        var principalType = fk.PrincipalType;

        // ---------- PK membership check ----------
        var pkAttr = principalType.GetCustomAttribute<VaultPrimaryKeyAttribute>(inherit: false);
        var isPk = pkAttr?.Keys is { Length: > 0 } && IsPropertyCoveredByKeys(principalType, fk.PrincipalPropertyName, pkAttr.Keys);

        // ---------- UNIQUE membership check (single-column only) ----------
        var uniqueAttrs = principalType.GetCustomAttributes<VaultUniqueKeyAttribute>(inherit: false).ToArray();
        var isUnique = uniqueAttrs.Any(uk =>
            uk.Keys is { Length: 1 } &&
            IsPropertyCoveredByKeys(principalType, fk.PrincipalPropertyName, uk.Keys!));

        if (!isPk && !isUnique)
        {
            Document.FailAndExit(
                $"Foreign key '{doc.Type.FullName}.{fk.PropertyName}' points to " +
                $"'{principalType.FullName}.{fk.PrincipalPropertyName}', " +
                "but that property is neither part of [VaultPrimaryKey] nor covered by a single-column [VaultUniqueKey]. " +
                "PostgreSQL requires referenced columns to be PRIMARY KEY or UNIQUE.");
        }
    }

    private static bool IsPropertyCoveredByKeys(Type principalType, string principalPropertyName, string[] keys)
    {
        var props = principalType.GetProperties(BindingFlags.Public | BindingFlags.Instance);

        var covered = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            // key might be property name
            var byName = props.FirstOrDefault(p => string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
            if (byName is not null)
            {
                covered.Add(byName.Name);
                continue;
            }

            // or physical column name
            var byColumn = props.FirstOrDefault(p =>
            {
                var colAttr = p.GetCustomAttribute<VaultColumnAttribute>(inherit: false);
                var physical = colAttr?.Name ?? p.Name.ToLowerInvariant();
                return string.Equals(physical, key, StringComparison.OrdinalIgnoreCase);
            });

            if (byColumn is not null)
                covered.Add(byColumn.Name);
        }

        return covered.Contains(principalPropertyName);
    }

    private static string NormalizeColumns(IEnumerable<string> columns) =>
        string.Join("|",
            columns
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.ToLowerInvariant())
                .OrderBy(c => c, StringComparer.Ordinal));
}
