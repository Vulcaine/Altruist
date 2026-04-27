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

[Service(typeof(IMigrationPlanner))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresMigrationPlanner : AbstractMigrationPlanner
{
    protected override string GetDefaultSchemaName() => "public";

    protected override string HistoryTimestampStoreType => "timestamptz";

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
