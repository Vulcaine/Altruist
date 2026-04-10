
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


using Altruist.Persistence;

namespace Altruist.Migrations;

public interface IMigrationExecutor
{
    Task ApplyAsync(string schema, IReadOnlyList<MigrationOperation> operations);
}

/// <summary>
/// Thrown when a migration operation fails. Contains the operation index,
/// operation type, and inner exception. The transaction is rolled back before this is thrown.
/// </summary>
public class MigrationException : Exception
{
    public MigrationException(string message) : base(message) { }
    public MigrationException(string message, Exception inner) : base(message, inner) { }
}

public abstract class AbstractMigrationExecutor : IMigrationExecutor
{
    protected readonly ISqlDatabaseProvider _provider;
    protected readonly int _batchSize;

    protected AbstractMigrationExecutor(
        ISqlDatabaseProvider provider,
        [AppConfigValue("altruist:persistence:migration:batch-size", "50000")] int batchSize = 50_000)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _batchSize = batchSize > 0 ? batchSize : 50_000;
    }

    public async Task ApplyAsync(string schema, IReadOnlyList<MigrationOperation> operations)
    {
        if (schema is null)
            throw new ArgumentNullException(nameof(schema));
        if (operations is null)
            throw new ArgumentNullException(nameof(operations));
        if (operations.Count == 0)
            return;

        await _provider.ConnectAsync();

        // Wrap all DDL in a transaction. Postgres supports transactional DDL
        // (CREATE TABLE, ALTER TABLE, etc.) so the entire migration is atomic.
        try
        {
            await _provider.ExecuteAsync("BEGIN;");

            for (int i = 0; i < operations.Count; i++)
            {
                try
                {
                    await ApplyOperationAsync(schema, operations[i]);
                }
                catch (Exception ex)
                {
                    try { await _provider.ExecuteAsync("ROLLBACK;"); } catch { /* best effort */ }
                    throw new MigrationException(
                        $"Migration failed at operation {i + 1}/{operations.Count}: {operations[i].GetType().Name}. " +
                        $"All changes rolled back. Error: {ex.Message}", ex);
                }
            }

            await _provider.ExecuteAsync("COMMIT;");
        }
        catch (MigrationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            try { await _provider.ExecuteAsync("ROLLBACK;"); } catch { /* best effort */ }
            throw new MigrationException($"Migration transaction failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Core dispatcher for migration operations. Provider-agnostic; delegates to
    /// provider-specific methods for actual SQL generation + execution.
    /// </summary>
    protected virtual Task ApplyOperationAsync(string defaultSchema, MigrationOperation op)
    {
        switch (op)
        {
            // --------------------------------
            // TABLE OPERATIONS
            // --------------------------------

            case CreateTableOperation createTable:
                return ApplyCreateTableAsync(defaultSchema, createTable);

            // (DropTableOperation support could be added here later if needed)

            // --------------------------------
            // COLUMN OPERATIONS
            // --------------------------------

            case AddColumnOperation addColumn:
                return ApplyAddColumnAsync(defaultSchema, addColumn);

            case DropColumnOperation dropColumn:
                return ApplyDropColumnAsync(defaultSchema, dropColumn);

            case RenameColumnOperation renameColumn:
                return ApplyRenameColumnAsync(defaultSchema, renameColumn);

            case AlterColumnTypeOperation alterType:
                return ApplyAlterColumnTypeAsync(defaultSchema, alterType);

            // --------------------------------
            // CONSTRAINT OPERATIONS
            // --------------------------------

            case AddUniqueConstraintOperation addUnique:
                return ApplyAddUniqueConstraintAsync(defaultSchema, addUnique);

            case DropConstraintOperation dropConstraint:
                return ApplyDropConstraintAsync(defaultSchema, dropConstraint);

            // --------------------------------
            // FOREIGN KEY OPERATIONS
            // --------------------------------

            case AddForeignKeyOperation addFk:
                return ApplyAddForeignKeyAsync(defaultSchema, addFk);

            case DropForeignKeyOperation dropFk:
                return ApplyDropForeignKeyAsync(defaultSchema, dropFk);

            // --------------------------------
            // INDEX OPERATIONS
            // --------------------------------

            case CreateIndexOperation createIndex:
                return ApplyCreateIndexAsync(defaultSchema, createIndex);

            case DropIndexOperation dropIndex:
                return ApplyDropIndexAsync(defaultSchema, dropIndex);

            default:
                throw new NotSupportedException(
                    $"Migration operation '{op.GetType().Name}' is not supported by {GetType().Name}.");
        }
    }

    // ---------- provider-specific implementations ----------

    protected abstract Task ApplyCreateTableAsync(string defaultSchema, CreateTableOperation op);

    protected abstract Task ApplyAddColumnAsync(string defaultSchema, AddColumnOperation op);

    protected abstract Task ApplyDropColumnAsync(string defaultSchema, DropColumnOperation op);

    protected abstract Task ApplyAddUniqueConstraintAsync(string defaultSchema, AddUniqueConstraintOperation op);

    protected abstract Task ApplyDropConstraintAsync(string defaultSchema, DropConstraintOperation op);

    protected abstract Task ApplyAddForeignKeyAsync(string defaultSchema, AddForeignKeyOperation op);

    protected abstract Task ApplyDropForeignKeyAsync(string defaultSchema, DropForeignKeyOperation op);

    protected abstract Task ApplyCreateIndexAsync(string defaultSchema, CreateIndexOperation op);

    protected abstract Task ApplyDropIndexAsync(string defaultSchema, DropIndexOperation op);

    protected abstract Task ApplyRenameColumnAsync(string defaultSchema, RenameColumnOperation op);

    protected abstract Task ApplyAlterColumnTypeAsync(string defaultSchema, AlterColumnTypeOperation op);

    protected abstract Task ApplyCreateSchemaAsync(string defaultSchema, CreateSchemaOperation createSchema);

    protected abstract Task ApplyDropTableAsync(string defaultSchema, DropTableOperation dropTable);
}
