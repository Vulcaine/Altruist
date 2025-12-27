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
    public static void Validate(VaultDocument doc)
    {
        if (doc is null)
            throw new ArgumentNullException(nameof(doc));

        ValidateStoredModel(doc);
        ValidateHasTypeProperty(doc);

        NormalizeUniqueKeys(doc);
        NormalizeIndexes(doc);

        ValidateForeignKeys(doc);
    }

    private static void ValidateStoredModel(VaultDocument doc)
    {
        if (!typeof(IStoredModel).IsAssignableFrom(doc.Type))
            VaultDocument.FailAndExit($"The type {doc.Type.FullName} must implement IModel.");
    }

    private static void ValidateHasTypeProperty(VaultDocument doc)
    {
        if (doc.Type.GetProperty("Type", BindingFlags.Public | BindingFlags.Instance) is null)
            VaultDocument.FailAndExit($"The type {doc.Type.FullName} must have a 'Type' property.");
    }

    private static void NormalizeUniqueKeys(VaultDocument doc)
    {
        // De-duplicate unique keys by normalized column-set (case-insensitive, order-insensitive)
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<VaultDocument.UniqueKeyDefinition>();

        foreach (var uk in doc.UniqueKeys)
        {
            var normalized = NormalizeColumns(uk.Columns);
            if (seen.Add(normalized))
                deduped.Add(uk);
        }

        doc.UniqueKeys.Clear();
        doc.UniqueKeys.AddRange(deduped);
    }

    private static void NormalizeIndexes(VaultDocument doc)
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

    private static void ValidateForeignKeys(VaultDocument doc)
    {
        if (doc.ForeignKeys is null || doc.ForeignKeys.Count == 0)
            return;

        foreach (var fk in doc.ForeignKeys)
        {
            ValidateOnDelete(doc, fk);

            // Build principal metadata via Document (canonical source of truth)
            var principalDoc = VaultDocument.From(fk.PrincipalType);

            ValidatePrincipalStoredModel(doc, fk, principalDoc);
            ValidatePrincipalColumnExists(doc, fk, principalDoc);
            ValidateReferencedColumnIsPkOrUnique(doc, fk, principalDoc);
        }
    }

    private static void ValidatePrincipalColumnExists(
        VaultDocument dependentDoc,
        VaultDocument.VaultForeignKeyDefinition fk,
        VaultDocument principalDoc)
    {
        var (_, principalPhysical) = ResolvePrincipalLogicalAndPhysical(principalDoc, fk.PrincipalPropertyName);

        if (principalPhysical is null)
        {
            VaultDocument.FailAndExit(
                $"Foreign key '{dependentDoc.Type.FullName}.{fk.PropertyName}' points to '{fk.PrincipalType.FullName}.{fk.PrincipalPropertyName}', " +
                "but that property/column does not exist on the principal document.");
        }
    }

    private static (string? Logical, string? Physical) ResolvePrincipalLogicalAndPhysical(
    VaultDocument principalDoc,
    string principalPropertyOrColumn)
    {
        if (string.IsNullOrWhiteSpace(principalPropertyOrColumn))
            return (null, null);

        // 1) Treat it as a logical property name first
        if (principalDoc.Columns.TryGetValue(principalPropertyOrColumn, out var physical))
            return (principalPropertyOrColumn, physical);

        // 2) Treat it as a physical column name
        var match = principalDoc.Columns.FirstOrDefault(kvp =>
            string.Equals(kvp.Value, principalPropertyOrColumn, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(match.Key))
            return (match.Key, match.Value);

        return (null, null);
    }

    private static void ValidatePrincipalStoredModel(
        VaultDocument dependentDoc,
        VaultDocument.VaultForeignKeyDefinition fk,
        VaultDocument principalDoc)
    {
        if (!typeof(IStoredModel).IsAssignableFrom(fk.PrincipalType))
        {
            VaultDocument.FailAndExit(
                $"Foreign key '{dependentDoc.Type.FullName}.{fk.PropertyName}' points to type '{fk.PrincipalType.FullName}' " +
                "which does not implement IStoredModel.");
        }

        // If principalDoc was built, it necessarily had a VaultAttribute (or derived),
        // because Document.From requires it. So we don't re-check attributes here.
        _ = principalDoc;
    }

    private static void ValidateOnDelete(VaultDocument doc, VaultDocument.VaultForeignKeyDefinition fk)
    {
        static bool IsValidOnDelete(string value) =>
            value.Equals("CASCADE", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("NO ACTION", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("RESTRICT", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("SET NULL", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("SET DEFAULT", StringComparison.OrdinalIgnoreCase);

        if (!IsValidOnDelete(fk.OnDelete))
        {
            VaultDocument.FailAndExit(
                $"Invalid OnDelete value '{fk.OnDelete}' on foreign key " +
                $"'{doc.Type.FullName}.{fk.PropertyName}'. " +
                "Allowed values are: CASCADE, NO ACTION, RESTRICT, SET NULL, SET DEFAULT.");
        }
    }

    private static void ValidateReferencedColumnIsPkOrUnique(
        VaultDocument dependentDoc,
        VaultDocument.VaultForeignKeyDefinition fk,
        VaultDocument principalDoc)
    {
        var (_, principalPhysical) = ResolvePrincipalLogicalAndPhysical(principalDoc, fk.PrincipalPropertyName);

        if (principalPhysical is null)
        {
            VaultDocument.FailAndExit(
                $"Foreign key '{dependentDoc.Type.FullName}.{fk.PropertyName}' points to '{fk.PrincipalType.FullName}.{fk.PrincipalPropertyName}', " +
                "but that property/column does not exist on the principal document.");
        }

        // ----- PK check (membership) -----
        var pkPhysicalCols = ResolveKeyColumnsToPhysical(principalDoc, principalDoc.PrimaryKey?.Keys);
        var isPk = principalPhysical != null && pkPhysicalCols.Contains(principalPhysical);

        // ----- Unique check (single-column UNIQUE constraints only) -----
        var isUnique = principalPhysical != null && principalDoc.UniqueKeys.Any(uk =>
            uk.Columns.Count == 1 &&
            StringEquals(uk.Columns[0], principalPhysical));

        if (!isPk && !isUnique)
        {
            VaultDocument.FailAndExit(
                $"Foreign key '{dependentDoc.Type.FullName}.{fk.PropertyName}' points to " +
                $"'{fk.PrincipalType.FullName}.{fk.PrincipalPropertyName}', " +
                "but that referenced column is neither PRIMARY KEY nor covered by a single-column UNIQUE constraint. " +
                "DB provider requires referenced columns to be PRIMARY KEY or UNIQUE.");
        }
    }

    private static HashSet<string> ResolveKeyColumnsToPhysical(VaultDocument principalDoc, string[]? keys)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (keys is null || keys.Length == 0)
            return set;

        foreach (var key in keys)
        {
            if (string.IsNullOrWhiteSpace(key))
                continue;

            // key is a logical property name
            if (principalDoc.Columns.TryGetValue(key, out var physicalFromLogical))
            {
                set.Add(physicalFromLogical);
                continue;
            }

            // key is a physical column name
            var match = principalDoc.Columns.Values.FirstOrDefault(v =>
                string.Equals(v, key, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrWhiteSpace(match))
                set.Add(match);
        }

        return set;
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


    private static bool StringEquals(string a, string b) =>
    string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
