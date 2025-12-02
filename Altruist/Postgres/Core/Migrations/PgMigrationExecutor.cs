// Altruist.Migrations.Postgres/PostgresMigrationExecutor.cs
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
*/

using Altruist.Persistence;

namespace Altruist.Migrations.Postgres;
// Altruist.Migrations.Postgres/PostgresMigrationExecutor.cs
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

[Service(typeof(IMigrationExecutor))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresMigrationExecutor : AbstractMigrationExecutor
{
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
        "ALTER TABLE {table_fqn} ADD CONSTRAINT {constraint_name} " +
        "FOREIGN KEY ({column_ident}) REFERENCES {principal_table_fqn} ({principal_column_ident}) ON DELETE {on_delete};";

    private const string DropForeignKeyTemplate =
        "ALTER TABLE {table_fqn} DROP CONSTRAINT IF EXISTS {constraint_name};";

    public PostgresMigrationExecutor(ISqlDatabaseProvider provider)
        : base(provider)
    {
    }

    // --------------------------------
    // TABLE OPERATIONS
    // --------------------------------

    protected override async Task ApplyCreateTableAsync(string defaultSchema, CreateTableOperation createTable)
    {
        // Use operation.Schema if set, otherwise fall back to keyspace name
        var schemaName = string.IsNullOrWhiteSpace(createTable.Schema)
            ? defaultSchema
            : createTable.Schema;

        var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(createTable.Table)}";

        // Build column definitions
        var colDefs = new List<string>(createTable.Columns.Count);
        foreach (var col in createTable.Columns)
        {
            // ColumnDefinition.StoreType is already provider-specific for Postgres
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
    }

    // --------------------------------
    // COLUMN OPERATIONS
    // --------------------------------

    protected override async Task ApplyAddColumnAsync(string defaultSchema, AddColumnOperation addCol)
    {
        var schemaName = string.IsNullOrWhiteSpace(addCol.Schema)
            ? defaultSchema
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
    }

    protected override async Task ApplyDropColumnAsync(string defaultSchema, DropColumnOperation dropCol)
    {
        var schemaName = string.IsNullOrWhiteSpace(dropCol.Schema)
            ? defaultSchema
            : dropCol.Schema;

        var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(dropCol.Table)}";

        var sql = AlterTableDropColumnTemplate
            .Replace("{table_fqn}", tableFqn)
            .Replace("{column_ident}", QuoteIdent(dropCol.ColumnName));

        await _provider.ExecuteAsync(sql);
    }

    // --------------------------------
    // CONSTRAINT OPERATIONS
    // --------------------------------

    protected override async Task ApplyAddUniqueConstraintAsync(string defaultSchema, AddUniqueConstraintOperation addUnique)
    {
        var schemaName = string.IsNullOrWhiteSpace(addUnique.Schema)
            ? defaultSchema
            : addUnique.Schema;

        var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(addUnique.Table)}";

        var sql = AddUniqueConstraintTemplate
            .Replace("{table_fqn}", tableFqn)
            .Replace("{constraint_name}", QuoteIdent(addUnique.ConstraintName))
            .Replace("{column_ident}", QuoteIdent(addUnique.Column));

        await _provider.ExecuteAsync(sql);
    }

    protected override async Task ApplyDropConstraintAsync(string defaultSchema, DropConstraintOperation dropConstraint)
    {
        var schemaName = string.IsNullOrWhiteSpace(dropConstraint.Schema)
            ? defaultSchema
            : dropConstraint.Schema;

        var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(dropConstraint.Table)}";

        var sql = DropConstraintTemplate
            .Replace("{table_fqn}", tableFqn)
            .Replace("{constraint_name}", QuoteIdent(dropConstraint.ConstraintName));

        await _provider.ExecuteAsync(sql);
    }

    // --------------------------------
    // FOREIGN KEY OPERATIONS
    // --------------------------------

    protected override async Task ApplyAddForeignKeyAsync(string defaultSchema, AddForeignKeyOperation addFk)
    {
        // Dependent (child) schema
        var dependentSchema = string.IsNullOrWhiteSpace(addFk.Schema)
            ? defaultSchema
            : addFk.Schema;

        // Principal (parent) schema – comes from the principal vault's keyspace.
        // Fallback to dependent schema if not set, just in case.
        var principalSchema = string.IsNullOrWhiteSpace(addFk.PrincipalSchema)
            ? dependentSchema
            : addFk.PrincipalSchema;

        var tableFqn = $"{QuoteIdent(dependentSchema)}.{QuoteIdent(addFk.Table)}";
        var principalTableFqn = $"{QuoteIdent(principalSchema)}.{QuoteIdent(addFk.PrincipalTable)}";

        var sql = AddForeignKeyTemplate
            .Replace("{table_fqn}", tableFqn)
            .Replace("{constraint_name}", QuoteIdent(addFk.ConstraintName))
            .Replace("{column_ident}", QuoteIdent(addFk.Column))
            .Replace("{principal_table_fqn}", principalTableFqn)
            .Replace("{principal_column_ident}", QuoteIdent(addFk.PrincipalColumn))
            .Replace("{on_delete}", addFk.OnDelete);

        await _provider.ExecuteAsync(sql);
    }

    protected override async Task ApplyDropForeignKeyAsync(string defaultSchema, DropForeignKeyOperation dropFk)
    {
        var schemaName = string.IsNullOrWhiteSpace(dropFk.Schema)
            ? defaultSchema
            : dropFk.Schema;

        var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(dropFk.Table)}";

        var sql = DropForeignKeyTemplate
            .Replace("{table_fqn}", tableFqn)
            .Replace("{constraint_name}", QuoteIdent(dropFk.ConstraintName));

        await _provider.ExecuteAsync(sql);
    }

    // --------------------------------
    // INDEX OPERATIONS
    // --------------------------------

    protected override async Task ApplyCreateIndexAsync(string defaultSchema, CreateIndexOperation createIndex)
    {
        var schemaName = string.IsNullOrWhiteSpace(createIndex.Schema)
            ? defaultSchema
            : createIndex.Schema;

        var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(createIndex.Table)}";

        var sql = CreateIndexTemplate
            .Replace("{index_name}", QuoteIdent(createIndex.IndexName))
            .Replace("{table_fqn}", tableFqn)
            .Replace("{column_ident}", QuoteIdent(createIndex.Column));

        await _provider.ExecuteAsync(sql);
    }

    protected override async Task ApplyDropIndexAsync(string defaultSchema, DropIndexOperation dropIndex)
    {
        // PostgreSQL index names are global per schema;
        // DROP INDEX does not require table name.
        var sql = DropIndexTemplate
            .Replace("{index_name}", QuoteIdent(dropIndex.IndexName));

        await _provider.ExecuteAsync(sql);
    }

    // --------------------------------
    // helpers
    // --------------------------------

    private static string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";
}
