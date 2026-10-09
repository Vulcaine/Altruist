/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
*/

using System.Globalization;
using System.Text.Json;

namespace Altruist.Migrations.Postgres;
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
*/

/// <summary>
/// Postgres <see cref="IMigrationPlanner"/>: the provider-agnostic <see cref="AbstractMigrationPlanner"/> diff
/// (desired vault documents vs. the live schema snapshot from <see cref="PostgresSchemaInspector"/>) plus the
/// Postgres CLR-to-column type mapping and default-value literals.
/// </summary>
/// <remarks>
/// <para>
/// Registered as a singleton <see cref="IMigrationPlanner"/> when <c>altruist:persistence:database:provider</c> is
/// <c>postgres</c>; used by the vault schema migrator at startup (see <see cref="Persistence.Postgres.PostgresDatabaseConfiguration"/>).
/// You do not call it directly.
/// </para>
/// <para>
/// The plan is not purely additive. Besides creating tables, columns, unique constraints, indexes and foreign
/// keys, it also plans <b>destructive</b> operations: columns present in the database but no longer on the model
/// are dropped (data lost) unless matched by <c>[VaultRenamedFrom]</c>; column type changes are altered in place;
/// unique constraints and <c>&lt;table&gt;_&lt;col&gt;_idx</c> indexes no longer declared are dropped; tables marked
/// <c>[VaultTableDelete]</c> are dropped and <c>[VaultArchived]</c> tables are copied then dropped.
/// </para>
/// <para>
/// Type map: <c>string</c>→<c>text</c>, <c>bool</c>→<c>boolean</c>, <c>byte</c>/<c>short</c>→<c>smallint</c>,
/// <c>int</c>→<c>integer</c>, <c>long</c>→<c>bigint</c>, <c>float</c>→<c>real</c>, <c>double</c>→<c>double precision</c>,
/// <c>decimal</c>→<c>numeric</c>, <c>DateTime</c>→<c>timestamp</c>, <c>DateTimeOffset</c>→<c>timestamptz</c>,
/// <c>Guid</c>→<c>uuid</c>, <c>byte[]</c>→<c>bytea</c>, <c>TimeSpan</c>→<c>interval</c>; arrays of
/// short/int/long/string/float/double/Guid map to Postgres arrays; enums→<c>integer</c>; every other array,
/// collection or object→<c>jsonb</c>. Nullable&lt;T&gt; maps like T. History tables use <c>timestamptz</c> for
/// their <c>timestamp</c> column.
/// </para>
/// </remarks>
[Service(typeof(IMigrationPlanner))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresMigrationPlanner : AbstractMigrationPlanner
{
    /// <inheritdoc/>
    /// <remarks>Postgres: <c>public</c>.</remarks>
    protected override string GetDefaultSchemaName() => "public";

    /// <inheritdoc/>
    /// <remarks>Postgres: <c>timestamptz</c>.</remarks>
    protected override string HistoryTimestampStoreType => "timestamptz";

    /// <inheritdoc/>
    /// <remarks>See the type map in the class remarks.</remarks>
    protected override string MapClrTypeToStoreType(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            type = Nullable.GetUnderlyingType(type)!;

        if (type == typeof(string))
            return "text";
        if (type == typeof(bool))
            return "boolean";
        if (type == typeof(byte))
            return "smallint";
        if (type == typeof(short))
            return "smallint";
        if (type == typeof(int))
            return "integer";
        if (type == typeof(long))
            return "bigint";
        if (type == typeof(float))
            return "real";
        if (type == typeof(double))
            return "double precision";
        if (type == typeof(decimal))
            return "numeric";
        if (type == typeof(DateTime))
            return "timestamp";
        if (type == typeof(DateTimeOffset))
            return "timestamptz";
        if (type == typeof(Guid))
            return "uuid";
        if (type == typeof(byte[]))
            return "bytea";
        if (type == typeof(TimeSpan))
            return "interval";

        if (type.IsArray)
        {
            var elem = type.GetElementType()!;

            if (elem == typeof(short))
                return "smallint[]";
            if (elem == typeof(int))
                return "integer[]";
            if (elem == typeof(long))
                return "bigint[]";
            if (elem == typeof(string))
                return "text[]";
            if (elem == typeof(float))
                return "real[]";
            if (elem == typeof(double))
                return "double precision[]";
            if (elem == typeof(Guid))
                return "uuid[]";

            return "jsonb";
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            return "jsonb";

        if (type.IsEnum)
            return "integer";

        return "jsonb";
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Strings are single-quoted with <c>'</c> doubled; numbers use invariant culture; <c>DateTime</c>/<c>DateTimeOffset</c>
    /// use the round-trip (<c>"O"</c>) format; enums become their integer value; anything else not listed is
    /// serialized to JSON and cast to <c>jsonb</c>. Returns null for a null value (no default).
    /// </remarks>
    protected override string? MapClrDefaultValueToStoreDefault(object? value, Type type)
    {
        if (value == null)
            return null;

        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            type = Nullable.GetUnderlyingType(type)!;

        if (type == typeof(string))
            return $"'{EscapeSqlLiteral((string)value)}'";
        if (type == typeof(bool))
            return (bool)value ? "true" : "false";
        if (type == typeof(byte) || type == typeof(short) || type == typeof(int) || type == typeof(long))
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        if (type == typeof(float) || type == typeof(double) || type == typeof(decimal))
            return Convert.ToString(value, CultureInfo.InvariantCulture);
        if (type == typeof(DateTime))
            return $"'{((DateTime)value).ToString("O", CultureInfo.InvariantCulture)}'";
        if (type == typeof(DateTimeOffset))
            return $"'{((DateTimeOffset)value).ToString("O", CultureInfo.InvariantCulture)}'";
        if (type == typeof(Guid))
            return $"'{value}'";
        if (type == typeof(TimeSpan))
            return $"'{value}'";
        if (type.IsEnum)
            return Convert.ToString(Convert.ToInt32(value, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);

        var json = JsonSerializer.Serialize(value);
        return $"'{EscapeSqlLiteral(json)}'::jsonb";
    }

    private static string EscapeSqlLiteral(string value)
        => value.Replace("'", "''");
}
