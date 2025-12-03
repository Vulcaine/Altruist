/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using Altruist.Persistence;

using Npgsql;

namespace Altruist.Migrations.Postgres;

[Service(typeof(ISchemaInspector))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresSchemaInspector : AbstractSchemaInspector
{
    public PostgresSchemaInspector(ISqlDatabaseProvider provider)
        : base(provider)
    {
    }

    protected override async Task<SchemaSnapshot> LoadSchemaAsync(string schemaName, CancellationToken ct)
    {
        var connString = _provider.GetConnectionString();

        await using var conn = new NpgsqlConnection(connString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var columnsByTable = await LoadColumnsAsync(conn, schemaName, ct).ConfigureAwait(false);
        var pkByTable = await LoadPrimaryKeysAsync(conn, schemaName, ct).ConfigureAwait(false);
        var uniqueByTable = await LoadUniqueConstraintsAsync(conn, schemaName, ct).ConfigureAwait(false);
        var indexesByTable = await LoadIndexesAsync(conn, schemaName, ct).ConfigureAwait(false);
        var foreignKeysByTable = await LoadForeignKeysAsync(conn, schemaName, ct).ConfigureAwait(false);

        return new SchemaSnapshot(
            columnsByTable,
            pkByTable,
            uniqueByTable,
            indexesByTable,
            foreignKeysByTable);
    }

    protected override string GetDefaultSchemaName() => "public";

    // ---------- Postgres-specific loaders ----------

    /// <summary>
    /// Load foreign keys for all tables whose constraints live in <paramref name="schemaName"/>.
    /// Handles cross-schema references correctly by joining via constraint_schema
    /// and letting ccu.table_schema be the principal table's schema (which we don't need here).
    /// </summary>
    private static async Task<Dictionary<string, List<ForeignKeyModel>>> LoadForeignKeysAsync(
        NpgsqlConnection conn,
        string schemaName,
        CancellationToken ct)
    {
        // table -> list of FK models
        var result = new Dictionary<string, List<ForeignKeyModel>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
        SELECT
            tc.table_name,
            tc.constraint_name,
            kcu.column_name,
            ccu.table_name  AS foreign_table_name,
            ccu.column_name AS foreign_column_name
        FROM information_schema.table_constraints AS tc
        JOIN information_schema.key_column_usage AS kcu
          ON tc.constraint_name   = kcu.constraint_name
         AND tc.constraint_schema = kcu.constraint_schema
        JOIN information_schema.constraint_column_usage AS ccu
          ON ccu.constraint_name   = tc.constraint_name
         AND ccu.constraint_schema = tc.constraint_schema
        WHERE tc.constraint_schema = @schema
          AND tc.constraint_type   = 'FOREIGN KEY';";

        cmd.Parameters.AddWithValue("schema", schemaName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var tableName = reader.GetString(0); // dependent table (in schemaName)
            var constraintName = reader.GetString(1);
            var columnName = reader.GetString(2); // dependent column
            var foreignTable = reader.GetString(3); // principal table name (schema may differ)
            var foreignColumn = reader.GetString(4); // principal column

            if (!result.TryGetValue(tableName, out var list))
            {
                list = new List<ForeignKeyModel>();
                result[tableName] = list;
            }

            list.Add(new ForeignKeyModel(
                name: constraintName,
                column: columnName,
                principalTable: foreignTable,
                principalColumn: foreignColumn));
        }

        return result;
    }

    private static async Task<Dictionary<string, Dictionary<string, ColumnModel>>> LoadColumnsAsync(
        NpgsqlConnection conn,
        string schemaName,
        CancellationToken ct)
    {
        var result = new Dictionary<string, Dictionary<string, ColumnModel>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT table_name, column_name, is_nullable, data_type
            FROM information_schema.columns
            WHERE table_schema = @schema;";
        cmd.Parameters.AddWithValue("schema", schemaName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var tableName = reader.GetString(0);
            var columnName = reader.GetString(1);
            var isNullable = string.Equals(reader.GetString(2), "YES", StringComparison.OrdinalIgnoreCase);
            var dataType = reader.GetString(3);

            if (!result.TryGetValue(tableName, out var colDict))
            {
                colDict = new Dictionary<string, ColumnModel>(StringComparer.OrdinalIgnoreCase);
                result[tableName] = colDict;
            }

            colDict[columnName] = new ColumnModel(columnName, dataType, isNullable);
        }

        return result;
    }

    private static async Task<Dictionary<string, List<string>>> LoadPrimaryKeysAsync(
        NpgsqlConnection conn,
        string schemaName,
        CancellationToken ct)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                tc.table_name,
                kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name
             AND tc.table_schema    = kcu.table_schema
             AND tc.table_name      = kcu.table_name
            WHERE tc.table_schema   = @schema
              AND tc.constraint_type = 'PRIMARY KEY';";
        cmd.Parameters.AddWithValue("schema", schemaName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var tableName = reader.GetString(0);
            var colName = reader.GetString(1);

            if (!result.TryGetValue(tableName, out var list))
            {
                list = new List<string>();
                result[tableName] = list;
            }

            list.Add(colName);
        }

        return result;
    }

    private static async Task<Dictionary<string, Dictionary<string, string>>> LoadUniqueConstraintsAsync(
        NpgsqlConnection conn,
        string schemaName,
        CancellationToken ct)
    {
        // table -> column -> constraintName
        var result = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT
                tc.table_name,
                tc.constraint_name,
                kcu.column_name
            FROM information_schema.table_constraints tc
            JOIN information_schema.key_column_usage kcu
              ON tc.constraint_name = kcu.constraint_name
             AND tc.table_schema    = kcu.table_schema
             AND tc.table_name      = kcu.table_name
            WHERE tc.table_schema   = @schema
              AND tc.constraint_type = 'UNIQUE';";
        cmd.Parameters.AddWithValue("schema", schemaName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var tableName = reader.GetString(0);
            var constraintName = reader.GetString(1);
            var columnName = reader.GetString(2);

            if (!result.TryGetValue(tableName, out var map))
            {
                map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                result[tableName] = map;
            }

            // This assumes 1-column UNIQUE constraints (which matches your model)
            map[columnName] = constraintName;
        }

        return result;
    }

    private static async Task<Dictionary<string, Dictionary<string, IndexModel>>> LoadIndexesAsync(
        NpgsqlConnection conn,
        string schemaName,
        CancellationToken ct)
    {
        // table -> indexName -> IndexModel
        var result = new Dictionary<string, Dictionary<string, IndexModel>>(StringComparer.OrdinalIgnoreCase);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
            SELECT tablename, indexname, indexdef
            FROM pg_indexes
            WHERE schemaname = @schema;";
        cmd.Parameters.AddWithValue("schema", schemaName);

        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var tableName = reader.GetString(0);
            var indexName = reader.GetString(1);
            var indexDef = reader.GetString(2);

            var column = ExtractFirstIndexColumn(indexDef);
            if (string.IsNullOrWhiteSpace(column))
                continue;

            if (!result.TryGetValue(tableName, out var ixDict))
            {
                ixDict = new Dictionary<string, IndexModel>(StringComparer.OrdinalIgnoreCase);
                result[tableName] = ixDict;
            }

            ixDict[indexName] = new IndexModel(indexName, column);
        }

        return result;
    }

    private static string ExtractFirstIndexColumn(string indexDef)
    {
        // Same parsing logic you had in GetExistingIndexesAsync
        var open = indexDef.IndexOf('(');
        var close = indexDef.IndexOf(')', open + 1);
        if (open < 0 || close <= open)
            return string.Empty;

        var inside = indexDef.Substring(open + 1, close - open - 1).Trim();
        if (inside.StartsWith("(", StringComparison.Ordinal))
        {
            var innerOpen = inside.IndexOf('(');
            var innerClose = inside.IndexOf(')', innerOpen + 1);
            if (innerOpen >= 0 && innerClose > innerOpen)
                return inside.Substring(innerOpen + 1, innerClose - innerOpen - 1).Trim('"');
        }
        else
        {
            var firstToken = inside.Split(',')[0].Trim();
            firstToken = firstToken.Split(' ')[0].Trim();
            return firstToken.Trim('"');
        }

        return string.Empty;
    }
}
