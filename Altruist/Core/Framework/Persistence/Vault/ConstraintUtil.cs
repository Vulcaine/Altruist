/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Security.Cryptography;
using System.Text;

namespace Altruist.Persistence;

/// <summary>
/// Shared helpers for deterministic DB constraint naming.
/// Must stay consistent with migration planner naming rules.
/// </summary>
public static class ConstraintUtil
{
    // Keep in sync with migration planner
    public const int MaxConstraintNameLength = 60;

    /// <summary>
    /// Postgres default name for a single-column UNIQUE constraint created inline on a column:
    ///   {table}_{column}_key
    /// Example:
    ///   item-catalog_catalog-code_key
    /// </summary>
    public static string BuildPostgresImplicitUniqueName(string tableName, string columnName)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name is required.", nameof(tableName));
        if (string.IsNullOrWhiteSpace(columnName))
            throw new ArgumentException("Column name is required.", nameof(columnName));

        // Postgres uses the identifiers as stored (usually lowercase unless quoted).
        // Your framework uses lower/hyphenated physical names, which matches the error names you see.
        return $"{tableName}_{columnName}_key";
    }

    /// <summary>
    /// Deterministic, safe-length naming for composite UNIQUE constraints created via ADD CONSTRAINT.
    /// Must match AbstractMigrationPlanner.BuildUniqueConstraintName(...) behavior.
    /// </summary>
    public static string BuildCompositeUniqueConstraintName(string tableName, IReadOnlyList<string> columns)
    {
        if (string.IsNullOrWhiteSpace(tableName))
            throw new ArgumentException("Table name is required.", nameof(tableName));
        if (columns is null || columns.Count == 0)
            throw new ArgumentException("At least one column is required.", nameof(columns));

        // Matches: ConstructConstraintName("uq", tableName, string.Join("_", columns))
        return ConstructConstraintName("uq", tableName, string.Join("_", columns));
    }

    /// <summary>
    /// Returns the UNIQUE constraint name that should exist for the given table+columns
    /// using your framework's migration rules:
    /// - 1 column: Postgres implicit {table}_{col}_key
    /// - 2+ columns: deterministic "uq_..." name (possibly hashed for length)
    /// </summary>
    public static string GetUniqueConstraintName(string tableName, IReadOnlyList<string> columns)
    {
        if (columns is null || columns.Count == 0)
            throw new ArgumentException("At least one column is required.", nameof(columns));

        return columns.Count == 1
            ? BuildPostgresImplicitUniqueName(tableName, columns[0])
            : BuildCompositeUniqueConstraintName(tableName, columns);
    }

    /// <summary>
    /// Same ConstructConstraintName algorithm used by the migration planner.
    /// Ensures <= MaxConstraintNameLength with deterministic hash suffix.
    /// </summary>
    public static string ConstructConstraintName(string prefix, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Constraint prefix must be provided.", nameof(prefix));

        static string NormalizePart(string s)
            => string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToLowerInvariant();

        var normalizedParts = parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizePart)
            .ToArray();

        var full = $"{prefix}_{string.Join("_", normalizedParts)}";

        if (full.Length <= MaxConstraintNameLength)
            return full;

        var hash = ShortHexHash(full, hexChars: 12);

        var reserve = 1 + hash.Length; // "_{hash}"
        var keepLen = MaxConstraintNameLength - reserve;

        var kept = full[..keepLen].TrimEnd('_');
        if (string.IsNullOrWhiteSpace(kept))
            kept = prefix.ToLowerInvariant();

        return $"{kept}_{hash}";
    }

    private static string ShortHexHash(string input, int hexChars)
    {
        if (hexChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(hexChars));

        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha.ComputeHash(bytes);

        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        return hexChars >= hex.Length ? hex : hex[..hexChars];
    }
}
