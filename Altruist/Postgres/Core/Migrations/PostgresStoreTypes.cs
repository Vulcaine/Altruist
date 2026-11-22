/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
*/

namespace Altruist.Migrations.Postgres;

/// <summary>
/// Canonical spellings of Postgres column types, shared by <see cref="PostgresSchemaInspector"/> (which reports
/// columns with them) and <see cref="PostgresMigrationPlanner"/> (which compares them with its type map).
/// </summary>
/// <remarks>
/// Use it when comparing a catalog type (<c>information_schema.columns.data_type</c> / <c>udt_name</c>, or
/// <c>format_type</c>) with a type written in DDL: Postgres reports <c>timestamp without time zone</c> for
/// <c>timestamp</c>, <c>ARRAY</c> plus an <c>_int4</c> udt for <c>integer[]</c>, <c>int4</c> for <c>integer</c>, and so on.
/// Type modifiers (e.g. <c>varchar(20)</c>) are kept; only aliases are folded. Pure and thread-safe.
/// </remarks>
public static class PostgresStoreTypes
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.Ordinal)
    {
        ["timestamp without time zone"] = "timestamp",
        ["timestamp with time zone"] = "timestamptz",
        ["time without time zone"] = "time",
        ["time with time zone"] = "timetz",
        ["int2"] = "smallint",
        ["int4"] = "integer",
        ["int"] = "integer",
        ["int8"] = "bigint",
        ["float4"] = "real",
        ["float8"] = "double precision",
        ["bool"] = "boolean",
        ["decimal"] = "numeric",
        ["character varying"] = "varchar",
        ["bpchar"] = "character",
        ["char"] = "character",
    };

    /// <summary>
    /// Folds a Postgres type name onto the spelling the Altruist type map uses: trimmed, lower-case, aliases
    /// replaced, udt array names (<c>_int4</c>) and <c>[]</c> suffixes normalized element-wise
    /// (<c>_float8</c> → <c>double precision[]</c>). A bare <c>ARRAY</c> becomes <c>array</c>.
    /// </summary>
    /// <param name="storeType">Type name from the catalog or from DDL; null is treated as empty.</param>
    /// <returns>The canonical type name.</returns>
    public static string Normalize(string? storeType)
    {
        var t = (storeType ?? string.Empty).Trim().ToLowerInvariant();
        if (t.Length == 0)
            return t;

        if (t.EndsWith("[]", StringComparison.Ordinal))
            return Normalize(t[..^2]) + "[]";

        // udt_name of an array type: "_" + element udt name.
        if (t.Length > 1 && t[0] == '_')
            return Normalize(t[1..]) + "[]";

        return Aliases.TryGetValue(t, out var canonical) ? canonical : t;
    }

    /// <summary>
    /// The canonical type of an <c>information_schema.columns</c> row: <paramref name="udtName"/> for arrays
    /// (<c>data_type = 'ARRAY'</c>, whose element type only the udt name carries), otherwise <paramref name="dataType"/>.
    /// </summary>
    /// <param name="dataType"><c>information_schema.columns.data_type</c>.</param>
    /// <param name="udtName"><c>information_schema.columns.udt_name</c>; may be null.</param>
    /// <returns>The canonical type name.</returns>
    public static string FromInformationSchema(string dataType, string? udtName)
    {
        if (string.Equals(dataType, "ARRAY", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(udtName))
            return Normalize(udtName);
        if (string.Equals(dataType, "USER-DEFINED", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(udtName))
            return Normalize(udtName);
        return Normalize(dataType);
    }
}
