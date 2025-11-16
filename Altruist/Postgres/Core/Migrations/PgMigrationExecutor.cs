// Altruist.Migrations.Postgres/PostgresMigrationExecutor.cs
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
*/

using System.Text;

using Altruist.Persistence;

namespace Altruist.Migrations.Postgres;

[Service(typeof(IMigrationExecutor))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresMigrationExecutor : IMigrationExecutor
{
    private readonly ISqlDatabaseProvider _provider;

    private const string CreateSchemaSqlTemplate =
        "CREATE SCHEMA IF NOT EXISTS {schema};";

    private const string CreateTableSqlTemplate =
        "CREATE TABLE IF NOT EXISTS {table_fqn} ({columns}, PRIMARY KEY ({pk_columns}));";

    private const string DropTableSqlTemplate =
        "DROP TABLE IF EXISTS {table_fqn} CASCADE;";

    private const string AlterTableAddColumnTemplate =
        "ALTER TABLE {table_fqn} ADD COLUMN IF NOT EXISTS {column_ident} {column_type};";

    private const string AlterTableDropColumnTemplate =
        "ALTER TABLE {table_fqn} DROP COLUMN IF EXISTS {column_ident} CASCADE;";

    private const string AddUniqueConstraintTemplate =
        "ALTER TABLE {table_fqn} ADD CONSTRAINT {constraint_name} UNIQUE ({column_ident});";

    private const string DropConstraintTemplate =
        "ALTER TABLE {table_fqn} DROP CONSTRAINT IF EXISTS {constraint_name};";

    private const string CreateIndexTemplate =
        "CREATE INDEX IF NOT EXISTS {index_name} ON {table_fqn} ({column_ident});";

    private const string DropIndexTemplate =
        "DROP INDEX IF EXISTS {index_name};";

    private const string AddForeignKeyTemplate =
    "ALTER TABLE {table_fqn} ADD CONSTRAINT {constraint_name} FOREIGN KEY ({column_ident}) REFERENCES {principal_table_fqn} ({principal_column_ident});";

    private const string DropForeignKeyTemplate =
        "ALTER TABLE {table_fqn} DROP CONSTRAINT IF EXISTS {constraint_name};";

    public PostgresMigrationExecutor(ISqlDatabaseProvider provider)
    {
        _provider = provider;
    }

    public async Task ApplyAsync(IKeyspace schema, IReadOnlyList<MigrationOperation> operations)
    {
        await _provider.ConnectAsync();

        foreach (var op in operations)
        {
            await ApplyOperationAsync(schema, op);
        }
    }

    private async Task ApplyOperationAsync(IKeyspace defaultSchema, MigrationOperation op)
    {
        switch (op)
        {
            // --------------------------------
            // TABLE OPERATIONS
            // --------------------------------

            case CreateTableOperation createTable:
                {
                    // Use operation.Schema if set, otherwise fall back to keyspace name
                    var schemaName = string.IsNullOrWhiteSpace(createTable.Schema)
                        ? defaultSchema.Name
                        : createTable.Schema;

                    var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(createTable.Table)}";

                    // Build column definitions
                    var colDefs = new List<string>(createTable.Columns.Count);
                    foreach (var col in createTable.Columns)
                    {
                        // NOTE: We assume ColumnDefinition.StoreType is already provider-specific
                        // for Postgres (e.g. "text", "integer", "jsonb", etc.).
                        var sb = new StringBuilder();
                        sb.Append(QuoteIdent(col.Name))
                          .Append(' ')
                          .Append(col.StoreType);

                        if (!col.IsNullable)
                            sb.Append(" NOT NULL");

                        if (col.IsUnique)
                            sb.Append(" UNIQUE");

                        colDefs.Add(sb.ToString());
                    }

                    if (createTable.PrimaryKeyColumns is null || createTable.PrimaryKeyColumns.Count == 0)
                        throw new InvalidOperationException(
                            $"CreateTableOperation for '{createTable.Table}' is missing primary key columns.");

                    var columnsSql = string.Join(", ", colDefs);
                    var pkSql = string.Join(", ", createTable.PrimaryKeyColumns.Select(QuoteIdent));

                    var sql = CreateTableSqlTemplate
                        .Replace("{table_fqn}", tableFqn)
                        .Replace("{columns}", columnsSql)
                        .Replace("{pk_columns}", pkSql);

                    await _provider.ExecuteAsync(sql);
                    break;
                }

            // --------------------------------
            // COLUMN OPERATIONS
            // --------------------------------

            case AddColumnOperation addCol:
                {
                    var schemaName = string.IsNullOrWhiteSpace(addCol.Schema)
                        ? defaultSchema.Name
                        : addCol.Schema;

                    var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(addCol.Table)}";
                    var column = addCol.Column;

                    var typeSegment =
                        column.StoreType +
                        (column.IsNullable ? "" : " NOT NULL") +
                        (column.IsUnique ? " UNIQUE" : "");

                    var sql = AlterTableAddColumnTemplate
                        .Replace("{table_fqn}", tableFqn)
                        .Replace("{column_ident}", QuoteIdent(column.Name))
                        .Replace("{column_type}", typeSegment);

                    await _provider.ExecuteAsync(sql);
                    break;
                }

            case DropColumnOperation dropCol:
                {
                    var schemaName = string.IsNullOrWhiteSpace(dropCol.Schema)
                        ? defaultSchema.Name
                        : dropCol.Schema;

                    var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(dropCol.Table)}";

                    var sql = AlterTableDropColumnTemplate
                        .Replace("{table_fqn}", tableFqn)
                        .Replace("{column_ident}", QuoteIdent(dropCol.ColumnName));

                    await _provider.ExecuteAsync(sql);
                    break;
                }

            // --------------------------------
            // CONSTRAINT OPERATIONS
            // --------------------------------

            case AddUniqueConstraintOperation addUnique:
                {
                    var schemaName = string.IsNullOrWhiteSpace(addUnique.Schema)
                        ? defaultSchema.Name
                        : addUnique.Schema;

                    var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(addUnique.Table)}";

                    var sql = AddUniqueConstraintTemplate
                        .Replace("{table_fqn}", tableFqn)
                        .Replace("{constraint_name}", QuoteIdent(addUnique.ConstraintName))
                        .Replace("{column_ident}", QuoteIdent(addUnique.Column));

                    await _provider.ExecuteAsync(sql);
                    break;
                }

            case DropConstraintOperation dropConstraint:
                {
                    var schemaName = string.IsNullOrWhiteSpace(dropConstraint.Schema)
                        ? defaultSchema.Name
                        : dropConstraint.Schema;

                    var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(dropConstraint.Table)}";

                    var sql = DropConstraintTemplate
                        .Replace("{table_fqn}", tableFqn)
                        .Replace("{constraint_name}", QuoteIdent(dropConstraint.ConstraintName));

                    await _provider.ExecuteAsync(sql);
                    break;
                }

            // ---------- NEW: FOREIGN KEY OPERATIONS ----------

            case AddForeignKeyOperation addFk:
                {
                    var schemaName = string.IsNullOrWhiteSpace(addFk.Schema)
                        ? defaultSchema.Name
                        : addFk.Schema;

                    var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(addFk.Table)}";
                    var principalTableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(addFk.PrincipalTable)}";

                    var sql = AddForeignKeyTemplate
                        .Replace("{table_fqn}", tableFqn)
                        .Replace("{constraint_name}", QuoteIdent(addFk.ConstraintName))
                        .Replace("{column_ident}", QuoteIdent(addFk.Column))
                        .Replace("{principal_table_fqn}", principalTableFqn)
                        .Replace("{principal_column_ident}", QuoteIdent(addFk.PrincipalColumn));

                    await _provider.ExecuteAsync(sql);
                    break;
                }

            case DropForeignKeyOperation dropFk:
                {
                    var schemaName = string.IsNullOrWhiteSpace(dropFk.Schema)
                        ? defaultSchema.Name
                        : dropFk.Schema;

                    var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(dropFk.Table)}";

                    var sql = DropForeignKeyTemplate
                        .Replace("{table_fqn}", tableFqn)
                        .Replace("{constraint_name}", QuoteIdent(dropFk.ConstraintName));

                    await _provider.ExecuteAsync(sql);
                    break;
                }

            // --------------------------------
            // INDEX OPERATIONS
            // --------------------------------

            case CreateIndexOperation createIndex:
                {
                    var schemaName = string.IsNullOrWhiteSpace(createIndex.Schema)
                        ? defaultSchema.Name
                        : createIndex.Schema;

                    var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(createIndex.Table)}";

                    var sql = CreateIndexTemplate
                        .Replace("{index_name}", QuoteIdent(createIndex.IndexName))
                        .Replace("{table_fqn}", tableFqn)
                        .Replace("{column_ident}", QuoteIdent(createIndex.Column));

                    await _provider.ExecuteAsync(sql);
                    break;
                }

            case DropIndexOperation dropIndex:
                {
                    // PostgreSQL index names are global per schema;
                    // DROP INDEX does not require table name.
                    var sql = DropIndexTemplate
                        .Replace("{index_name}", QuoteIdent(dropIndex.IndexName));

                    await _provider.ExecuteAsync(sql);
                    break;
                }

            default:
                throw new NotSupportedException(
                    $"Migration operation '{op.GetType().Name}' is not supported by PostgresMigrationExecutor.");
        }
    }

    private static string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";
}
