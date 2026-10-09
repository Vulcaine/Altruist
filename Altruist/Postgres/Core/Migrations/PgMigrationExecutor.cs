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

using System.Text;

using Altruist.Persistence;

namespace Altruist.Migrations.Postgres
{
    /// <summary>
    /// Postgres <see cref="IMigrationExecutor"/>: turns each planned <see cref="MigrationOperation"/> into DDL/DML and
    /// runs it through the <see cref="ISqlDatabaseProvider"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Registered as a singleton <see cref="IMigrationExecutor"/> when <c>altruist:persistence:database:provider</c> is
    /// <c>postgres</c>; invoked by the vault schema migrator during startup bootstrap (see
    /// <see cref="Persistence.Postgres.PostgresDatabaseConfiguration"/>), never needed directly.
    /// </para>
    /// <para>
    /// The base class runs all operations of one schema inside a single transaction on one connection (Postgres DDL is
    /// transactional), so a failing operation rolls the whole schema's migration back and surfaces as a
    /// <see cref="MigrationException"/>. Creation statements are idempotent (<c>IF NOT EXISTS</c>); drops use
    /// <c>IF EXISTS</c>, and table/column drops use <c>CASCADE</c> (dependent views, constraints and foreign keys go too).
    /// </para>
    /// <para>
    /// Config: <c>altruist:persistence:migration:batch-size</c> (default 50000) is the row batch for data-copying
    /// operations.
    /// </para>
    /// </remarks>
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
            "ALTER TABLE {table_fqn} ADD CONSTRAINT {constraint_name} UNIQUE ({column_list});";

        private const string DropConstraintTemplate =
            "ALTER TABLE {table_fqn} DROP CONSTRAINT IF EXISTS {constraint_name};";

        // The new index name must be unqualified: Postgres always creates it in the table's schema
        // (a schema-qualified name is a syntax error).
        private const string CreateIndexTemplate =
            "CREATE INDEX IF NOT EXISTS {index_name} ON {table_fqn} ({column_ident});";

        private const string DropIndexTemplate =
            "DROP INDEX IF EXISTS {index_fqn};";

        private const string AddForeignKeyTemplate =
            "ALTER TABLE {table_fqn} ADD CONSTRAINT {constraint_name} " +
            "FOREIGN KEY ({column_ident}) REFERENCES {principal_table_fqn} ({principal_column_ident}) ON DELETE {on_delete};";

        private const string DropForeignKeyTemplate =
            "ALTER TABLE {table_fqn} DROP CONSTRAINT IF EXISTS {constraint_name};";

        /// <summary>Creates the executor.</summary>
        /// <param name="provider">Provider that executes the statements (and supplies the transaction).</param>
        /// <param name="batchSize">Rows per batch for column copies (<c>altruist:persistence:migration:batch-size</c>); values &lt;= 0 mean 50000.</param>
        public PostgresMigrationExecutor(
            ISqlDatabaseProvider provider,
            [AppConfigValue("altruist:persistence:migration:batch-size", "50000")] int batchSize = 50_000)
            : base(provider, batchSize)
        {
        }

        // --------------------------------
        // ARCHIVE TABLE ([VaultArchived])
        // --------------------------------

        /// <inheritdoc/>
        /// <remarks>
        /// <c>CREATE TABLE IF NOT EXISTS archive (LIKE source INCLUDING ALL)</c> then <c>INSERT INTO archive SELECT * FROM source</c>.
        /// Dropping the source is a separate planned operation.
        /// </remarks>
        protected override async Task ApplyArchiveTableAsync(string defaultSchema, ArchiveTableOperation op)
        {
            var schemaName = string.IsNullOrWhiteSpace(op.Schema) ? defaultSchema : op.Schema;
            var sourceFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(op.SourceTable)}";
            var archiveFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(op.ArchiveTable)}";

            // Create archive table with same structure, then copy all data
            await _provider.ExecuteAsync(
                $"CREATE TABLE IF NOT EXISTS {archiveFqn} (LIKE {sourceFqn} INCLUDING ALL);");
            await _provider.ExecuteAsync(
                $"INSERT INTO {archiveFqn} SELECT * FROM {sourceFqn};");
        }

        // --------------------------------
        // TABLE OPERATIONS
        // --------------------------------

        /// <inheritdoc/>
        /// <remarks><c>CREATE TABLE IF NOT EXISTS</c> with column defaults, <c>NOT NULL</c>, inline <c>UNIQUE</c> and a primary key.</remarks>
        /// <exception cref="InvalidOperationException">The operation has no primary-key columns.</exception>
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

                if (!string.IsNullOrWhiteSpace(col.DefaultSql))
                    sb.Append(" DEFAULT ").Append(col.DefaultSql);

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

        /// <inheritdoc/>
        /// <remarks>Destructive: <c>DROP TABLE IF EXISTS ... CASCADE</c>.</remarks>
        protected override async Task ApplyDropTableAsync(string defaultSchema, DropTableOperation dropTable)
        {
            var schemaName = string.IsNullOrWhiteSpace(dropTable.Schema)
                ? defaultSchema
                : dropTable.Schema;

            var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(dropTable.Table)}";

            var sql = DropTableSqlTemplate
                .Replace("{table_fqn}", tableFqn);

            await _provider.ExecuteAsync(sql);
        }

        // --------------------------------
        // COLUMN OPERATIONS
        // --------------------------------

        /// <inheritdoc/>
        /// <remarks><c>ADD COLUMN IF NOT EXISTS</c>. A <c>NOT NULL</c> column without a default fails on a non-empty table.</remarks>
        protected override async Task ApplyAddColumnAsync(string defaultSchema, AddColumnOperation addCol)
        {
            var schemaName = string.IsNullOrWhiteSpace(addCol.Schema)
                ? defaultSchema
                : addCol.Schema;

            var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(addCol.Table)}";
            var column = addCol.Column;

            var typeSegment =
                column.StoreType +
                (string.IsNullOrWhiteSpace(column.DefaultSql) ? "" : " DEFAULT " + column.DefaultSql) +
                (column.IsNullable ? "" : " NOT NULL") +
                (column.IsUnique ? " UNIQUE" : "");

            var sql = AlterTableAddColumnTemplate
                .Replace("{table_fqn}", tableFqn)
                .Replace("{column_ident}", QuoteIdent(column.Name))
                .Replace("{column_type}", typeSegment);

            await _provider.ExecuteAsync(sql);
        }

        /// <inheritdoc/>
        /// <remarks>Destructive: <c>DROP COLUMN IF EXISTS ... CASCADE</c>.</remarks>
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
        // RENAME COLUMN
        // --------------------------------

        /// <inheritdoc/>
        /// <remarks>Metadata-only <c>RENAME COLUMN</c>; data is preserved.</remarks>
        protected override async Task ApplyRenameColumnAsync(string defaultSchema, RenameColumnOperation op)
        {
            var schemaName = string.IsNullOrWhiteSpace(op.Schema) ? defaultSchema : op.Schema;
            var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(op.Table)}";

            // Direct metadata-only rename — no data copy needed
            var sql = $"ALTER TABLE {tableFqn} RENAME COLUMN {QuoteIdent(op.OldColumnName)} TO {QuoteIdent(op.NewColumnName)};";
            await _provider.ExecuteAsync(sql);
        }

        // --------------------------------
        // ALTER COLUMN TYPE
        // --------------------------------

        /// <inheritdoc/>
        /// <remarks>
        /// Within one type family (integers, floats, text, timestamps, numeric): <c>ALTER COLUMN ... TYPE ... USING col::type</c>.
        /// Across families: adds a temporary column, copies with a cast in batches of the configured size
        /// (<see cref="BuildBatchedCopySql"/>), drops the
        /// old column (<c>CASCADE</c>) and renames the temporary one; values that cannot be cast make the migration fail.
        /// </remarks>
        protected override async Task ApplyAlterColumnTypeAsync(string defaultSchema, AlterColumnTypeOperation op)
        {
            var schemaName = string.IsNullOrWhiteSpace(op.Schema) ? defaultSchema : op.Schema;
            var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(op.Table)}";
            var colIdent = QuoteIdent(op.ColumnName);

            if (IsSameTypeFamily(op.OldStoreType, op.NewStoreType))
            {
                // Widening within same family — Postgres handles without full table rewrite
                await _provider.ExecuteAsync(
                    $"ALTER TABLE {tableFqn} ALTER COLUMN {colIdent} TYPE {op.NewStoreType} USING {colIdent}::{op.NewStoreType};");
            }
            else
            {
                // Cross-type: batched copy to avoid long ACCESS EXCLUSIVE lock on large tables
                var tempCol = QuoteIdent($"_altruist_migrate_{op.ColumnName}");

                // 1. Add temp column with new type
                await _provider.ExecuteAsync(
                    $"ALTER TABLE {tableFqn} ADD COLUMN IF NOT EXISTS {tempCol} {op.NewStoreType};");

                // 2. Batched copy with cast (bounds the size of each statement)
                await CopyInBatchesAsync(tableFqn, colIdent, tempCol, op.NewStoreType);

                // 3. Drop old, rename temp
                await _provider.ExecuteAsync(
                    $"ALTER TABLE {tableFqn} DROP COLUMN {colIdent} CASCADE;");
                await _provider.ExecuteAsync(
                    $"ALTER TABLE {tableFqn} RENAME COLUMN {tempCol} TO {colIdent};");
            }
        }

        private static bool IsSameTypeFamily(string oldType, string newType)
        {
            var old = oldType.ToLowerInvariant().Trim();
            var @new = newType.ToLowerInvariant().Trim();

            var intFamily = new HashSet<string> { "smallint", "integer", "bigint", "int", "int4", "int8", "int2" };
            if (intFamily.Contains(old) && intFamily.Contains(@new)) return true;

            var floatFamily = new HashSet<string> { "real", "double precision", "float4", "float8" };
            if (floatFamily.Contains(old) && floatFamily.Contains(@new)) return true;

            var textFamily = new HashSet<string> { "text", "varchar", "character varying", "char", "character" };
            if (textFamily.Contains(old) && textFamily.Contains(@new)) return true;

            var tsFamily = new HashSet<string> { "timestamp", "timestamptz", "timestamp without time zone", "timestamp with time zone" };
            if (tsFamily.Contains(old) && tsFamily.Contains(@new)) return true;

            var numFamily = new HashSet<string> { "numeric", "decimal" };
            if (numFamily.Contains(old) && numFamily.Contains(@new)) return true;

            return false;
        }

        // --------------------------------
        // COPY COLUMN DATA ([VaultColumnCopy])
        // --------------------------------

        /// <inheritdoc/>
        /// <remarks>Batched <c>UPDATE target = source::type</c> for rows where the target is null and the source is not (see <see cref="BuildBatchedCopySql"/>).</remarks>
        protected override async Task ApplyCopyColumnDataAsync(string defaultSchema, CopyColumnDataOperation op)
        {
            var schemaName = string.IsNullOrWhiteSpace(op.Schema) ? defaultSchema : op.Schema;
            var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(op.Table)}";
            var srcIdent = QuoteIdent(op.SourceColumn);
            var tgtIdent = QuoteIdent(op.TargetColumn);

            await CopyInBatchesAsync(tableFqn, srcIdent, tgtIdent, op.TargetStoreType);
        }

        private async Task CopyInBatchesAsync(string tableFqn, string srcIdent, string tgtIdent, string storeType)
        {
            var sql = BuildBatchedCopySql(tableFqn, srcIdent, tgtIdent, storeType, _batchSize);
            while (true)
            {
                var affected = await _provider.ExecuteAsync(sql);
                // Every batch only picks rows whose cast is non-null, so updated rows never qualify
                // again and the loop always terminates (an empty batch means nothing is left).
                if (affected <= 0) break;
            }
        }

        /// <summary>
        /// The statement one batch of a column copy runs. Postgres has no <c>UPDATE ... LIMIT</c>, so a batch selects up
        /// to <paramref name="batchSize"/> row locations (<c>ctid</c>) whose target is NULL and whose source casts to a
        /// non-NULL value, and updates those rows. The outer statement re-checks the row conditions, so a row that a
        /// concurrent writer moved or changed between the sub-select and the update is skipped (picked up by a later
        /// batch) instead of being overwritten; inside the migration transaction the preceding DDL usually holds the
        /// table lock anyway. Exposed for tests and for providers deriving the same SQL.
        /// </summary>
        /// <param name="tableFqn">Quoted, schema-qualified table.</param>
        /// <param name="srcIdent">Quoted source column.</param>
        /// <param name="tgtIdent">Quoted target column.</param>
        /// <param name="storeType">Target store type used for the cast.</param>
        /// <param name="batchSize">Maximum rows per statement.</param>
        /// <returns>The SQL statement.</returns>
        public static string BuildBatchedCopySql(string tableFqn, string srcIdent, string tgtIdent, string storeType, int batchSize) =>
            $"UPDATE {tableFqn} SET {tgtIdent} = {srcIdent}::{storeType} " +
            $"WHERE ctid = ANY(ARRAY(SELECT ctid FROM {tableFqn} " +
            $"WHERE {tgtIdent} IS NULL AND {srcIdent} IS NOT NULL AND ({srcIdent}::{storeType}) IS NOT NULL " +
            $"LIMIT {batchSize})) AND {tgtIdent} IS NULL AND {srcIdent} IS NOT NULL;";

        // --------------------------------
        // RELAX NOT NULL (kept unmapped columns)
        // --------------------------------

        /// <inheritdoc/>
        /// <remarks><c>ALTER TABLE ... ALTER COLUMN ... DROP NOT NULL</c>; metadata only, no data changes.</remarks>
        protected override async Task ApplyRelaxNotNullAsync(string defaultSchema, RelaxNotNullOperation op)
        {
            var schemaName = string.IsNullOrWhiteSpace(op.Schema) ? defaultSchema : op.Schema;
            var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(op.Table)}";
            await _provider.ExecuteAsync(
                $"ALTER TABLE {tableFqn} ALTER COLUMN {QuoteIdent(op.ColumnName)} DROP NOT NULL;");
        }

        // --------------------------------
        // DELETE MARKED COLUMN ([VaultColumnDelete])
        // --------------------------------

        /// <inheritdoc/>
        /// <remarks>Destructive: <c>DROP COLUMN IF EXISTS ... CASCADE</c>.</remarks>
        protected override async Task ApplyDeleteMarkedColumnAsync(string defaultSchema, DeleteMarkedColumnOperation op)
        {
            var schemaName = string.IsNullOrWhiteSpace(op.Schema) ? defaultSchema : op.Schema;
            var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(op.Table)}";

            await _provider.ExecuteAsync(
                $"ALTER TABLE {tableFqn} DROP COLUMN IF EXISTS {QuoteIdent(op.ColumnName)} CASCADE;");
        }

        // --------------------------------
        // CONSTRAINT OPERATIONS
        // --------------------------------

        /// <inheritdoc/>
        /// <remarks><c>ADD CONSTRAINT ... UNIQUE (...)</c>; fails if existing rows violate it.</remarks>
        protected override async Task ApplyAddUniqueConstraintAsync(
            string defaultSchema,
            AddUniqueConstraintOperation addUnique)
        {
            var schemaName = string.IsNullOrWhiteSpace(addUnique.Schema)
                ? defaultSchema
                : addUnique.Schema;

            var tableFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(addUnique.Table)}";

            var columnList = string.Join(", ", addUnique.Columns.Select(QuoteIdent));

            var sql = AddUniqueConstraintTemplate
                .Replace("{table_fqn}", tableFqn)
                .Replace("{constraint_name}", QuoteIdent(addUnique.ConstraintName))
                .Replace("{column_list}", columnList);

            await _provider.ExecuteAsync(sql);
        }

        /// <inheritdoc/>
        protected override async Task ApplyDropConstraintAsync(
            string defaultSchema,
            DropConstraintOperation dropConstraint)
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

        /// <inheritdoc/>
        /// <remarks>The principal schema defaults to the dependent table's schema when the operation leaves it empty.</remarks>
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

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        /// <remarks>Single-column B-tree <c>CREATE INDEX IF NOT EXISTS</c> in the table's schema.</remarks>
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

        /// <inheritdoc/>
        protected override async Task ApplyDropIndexAsync(string defaultSchema, DropIndexOperation dropIndex)
        {
            var schemaName = string.IsNullOrWhiteSpace(dropIndex.Schema)
                ? defaultSchema
                : dropIndex.Schema;

            var indexFqn = $"{QuoteIdent(schemaName)}.{QuoteIdent(dropIndex.IndexName)}";

            var sql = DropIndexTemplate
                .Replace("{index_fqn}", indexFqn);

            await _provider.ExecuteAsync(sql);
        }

        // --------------------------------
        // SCHEMA CREATION (if needed)
        // --------------------------------

        /// <inheritdoc/>
        /// <remarks><c>CREATE SCHEMA IF NOT EXISTS</c> (name quoted, case preserved).</remarks>
        protected override async Task ApplyCreateSchemaAsync(string defaultSchema, CreateSchemaOperation createSchema)
        {
            var schemaName = string.IsNullOrWhiteSpace(createSchema.Schema)
                ? defaultSchema
                : createSchema.Schema;

            var sql = CreateSchemaSqlTemplate
                .Replace("{schema}", QuoteIdent(schemaName));

            await _provider.ExecuteAsync(sql);
        }

        // --------------------------------
        // helpers
        // --------------------------------

        private static string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";
    }
}
