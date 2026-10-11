
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

/// <summary>
/// Applies a planned list of <see cref="MigrationOperation"/>s to the database. Provider-specific (Postgres:
/// <c>PgMigrationExecutor</c>, registered with <c>[Service(typeof(IMigrationExecutor))]</c>); called by
/// <see cref="VaultSchemaMigrator"/>. Implement it (usually by deriving from <see cref="AbstractMigrationExecutor"/>) only
/// when adding a database provider.
/// </summary>
public interface IMigrationExecutor
{
    /// <summary>Applies the operations in list order.</summary>
    /// <param name="schema">Default schema for operations whose own schema is blank.</param>
    /// <param name="operations">Operations in execution order.</param>
    /// <returns>A task completing when every operation was applied.</returns>
    /// <exception cref="MigrationException">An operation failed.</exception>
    Task ApplyAsync(string schema, IReadOnlyList<MigrationOperation> operations);
}

/// <summary>
/// Thrown when a migration operation fails. Contains the operation index,
/// operation type, and inner exception. The transaction is rolled back before this is thrown.
/// </summary>
/// <remarks>
/// Rollback applies only when the provider supports transactions (<see cref="ISqlTransactionProvider"/>); otherwise
/// the message states that earlier operations were applied. Propagates out of the provider's startup bootstrap (it is not caught there).
/// </remarks>
public class MigrationException : Exception
{
    /// <summary>Creates the exception.</summary>
    /// <param name="message">Error message.</param>
    public MigrationException(string message) : base(message) { }
    /// <summary>Creates the exception wrapping the failing provider error.</summary>
    /// <param name="message">Error message (includes the operation index and type).</param>
    /// <param name="inner">The original error.</param>
    public MigrationException(string message, Exception inner) : base(message, inner) { }
}

/// <summary>
/// Base <see cref="IMigrationExecutor"/>: connects, runs every operation inside one transaction when the provider
/// implements <see cref="ISqlTransactionProvider"/> (all-or-nothing, relies on transactional DDL), and dispatches each
/// operation to the matching abstract <c>Apply*Async</c> hook.
/// </summary>
/// <remarks>
/// Without transaction support operations run one by one and earlier ones stay applied after a failure.
/// Reads <c>altruist:persistence:migration:batch-size</c> (default 50000; non-positive values fall back to it) for
/// batched data copies.
/// </remarks>
public abstract class AbstractMigrationExecutor : IMigrationExecutor
{
    /// <summary>Provider the DDL is executed on.</summary>
    protected readonly ISqlDatabaseProvider _provider;
    /// <summary>Rows per batch for data-copy operations (<c>altruist:persistence:migration:batch-size</c>).</summary>
    protected readonly int _batchSize;

    /// <summary>Creates the executor.</summary>
    /// <param name="provider">Provider to execute on.</param>
    /// <param name="batchSize">Rows per batch for data copies; bound from <c>altruist:persistence:migration:batch-size</c>.</param>
    /// <exception cref="ArgumentNullException"><paramref name="provider"/> is null.</exception>
    protected AbstractMigrationExecutor(
        ISqlDatabaseProvider provider,
        [AppConfigValue("altruist:persistence:migration:batch-size", "50000")] int batchSize = 50_000)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _batchSize = batchSize > 0 ? batchSize : 50_000;
    }

    /// <inheritdoc/>
    /// <remarks>No-op for an empty list. Calls <c>ConnectAsync</c> on the provider first.</remarks>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> or <paramref name="operations"/> is null.</exception>
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
        // The transaction must be bound to one connection: issuing "BEGIN;"/"COMMIT;" through
        // ExecuteAsync ran each statement on a different pooled connection (no atomicity at all).
        if (_provider is not ISqlTransactionProvider transactional)
        {
            for (int i = 0; i < operations.Count; i++)
                await ApplyOrWrapAsync(schema, operations, i, atomic: false);
            return;
        }

        await transactional.InTransactionAsync(async _ =>
        {
            for (int i = 0; i < operations.Count; i++)
                await ApplyOrWrapAsync(schema, operations, i, atomic: true);
            return true;
        });
    }

    private async Task ApplyOrWrapAsync(string schema, IReadOnlyList<MigrationOperation> operations, int i, bool atomic)
    {
        try
        {
            await ApplyOperationAsync(schema, operations[i]);
        }
        catch (Exception ex)
        {
            throw new MigrationException(
                $"Migration failed at operation {i + 1}/{operations.Count}: {operations[i].GetType().Name}. " +
                (atomic ? "All changes rolled back. " : "Earlier operations were applied. ") + $"Error: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Core dispatcher for migration operations. Provider-agnostic; delegates to
    /// provider-specific methods for actual SQL generation + execution.
    /// </summary>
    /// <remarks>
    /// Every built-in operation type is dispatched (including <see cref="CreateSchemaOperation"/>,
    /// <see cref="DropTableOperation"/> and <see cref="RelaxNotNullOperation"/>). Override to support additional
    /// operation types, falling back to <c>base.ApplyOperationAsync</c>.
    /// </remarks>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation to apply.</param>
    /// <returns>A task completing when the operation was applied.</returns>
    /// <exception cref="NotSupportedException">The operation type is not dispatched.</exception>
    protected virtual Task ApplyOperationAsync(string defaultSchema, MigrationOperation op)
    {
        switch (op)
        {
            // --------------------------------
            // TABLE OPERATIONS
            // --------------------------------

            case ArchiveTableOperation archiveTable:
                return ApplyArchiveTableAsync(defaultSchema, archiveTable);

            case CreateTableOperation createTable:
                return ApplyCreateTableAsync(defaultSchema, createTable);

            case DropTableOperation dropTable:
                return ApplyDropTableAsync(defaultSchema, dropTable);

            case CreateSchemaOperation createSchema:
                return ApplyCreateSchemaAsync(defaultSchema, createSchema);

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

            case CopyColumnDataOperation copyData:
                return ApplyCopyColumnDataAsync(defaultSchema, copyData);

            case DeleteMarkedColumnOperation deleteMarked:
                return ApplyDeleteMarkedColumnAsync(defaultSchema, deleteMarked);

            case RelaxNotNullOperation relaxNotNull:
                return ApplyRelaxNotNullAsync(defaultSchema, relaxNotNull);

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

    /// <summary>Executes a <see cref="CreateTableOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyCreateTableAsync(string defaultSchema, CreateTableOperation op);

    /// <summary>Executes an <see cref="AddColumnOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyAddColumnAsync(string defaultSchema, AddColumnOperation op);

    /// <summary>Executes a <see cref="DropColumnOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyDropColumnAsync(string defaultSchema, DropColumnOperation op);

    /// <summary>Executes an <see cref="AddUniqueConstraintOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyAddUniqueConstraintAsync(string defaultSchema, AddUniqueConstraintOperation op);

    /// <summary>Executes a <see cref="DropConstraintOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyDropConstraintAsync(string defaultSchema, DropConstraintOperation op);

    /// <summary>Executes an <see cref="AddForeignKeyOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyAddForeignKeyAsync(string defaultSchema, AddForeignKeyOperation op);

    /// <summary>Executes a <see cref="DropForeignKeyOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyDropForeignKeyAsync(string defaultSchema, DropForeignKeyOperation op);

    /// <summary>Executes a <see cref="CreateIndexOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyCreateIndexAsync(string defaultSchema, CreateIndexOperation op);

    /// <summary>Executes a <see cref="DropIndexOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyDropIndexAsync(string defaultSchema, DropIndexOperation op);

    /// <summary>Executes a <see cref="RenameColumnOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyRenameColumnAsync(string defaultSchema, RenameColumnOperation op);

    /// <summary>Executes a <see cref="CopyColumnDataOperation"/> (batched by <see cref="_batchSize"/>).</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyCopyColumnDataAsync(string defaultSchema, CopyColumnDataOperation op);

    /// <summary>Executes a <see cref="DeleteMarkedColumnOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyDeleteMarkedColumnAsync(string defaultSchema, DeleteMarkedColumnOperation op);

    /// <summary>Executes an <see cref="AlterColumnTypeOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyAlterColumnTypeAsync(string defaultSchema, AlterColumnTypeOperation op);

    /// <summary>Executes an <see cref="ArchiveTableOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyArchiveTableAsync(string defaultSchema, ArchiveTableOperation op);

    /// <summary>Executes a <see cref="CreateSchemaOperation"/>.</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="createSchema">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyCreateSchemaAsync(string defaultSchema, CreateSchemaOperation createSchema);

    /// <summary>Executes a <see cref="DropTableOperation"/> (planned for <see cref="Altruist.UORM.VaultTableDeleteAttribute"/> and, after the copy, <see cref="Altruist.UORM.VaultArchivedAttribute"/>).</summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="dropTable">The operation.</param>
    /// <returns>A task.</returns>
    protected abstract Task ApplyDropTableAsync(string defaultSchema, DropTableOperation dropTable);

    /// <summary>
    /// Executes a <see cref="RelaxNotNullOperation"/> (drop the column's NOT NULL constraint). Virtual so existing
    /// providers keep compiling; the default throws <see cref="NotSupportedException"/>, which aborts the migration —
    /// override it in every provider whose planner keeps unmapped columns.
    /// </summary>
    /// <param name="defaultSchema">Schema used when the operation's schema is blank.</param>
    /// <param name="op">The operation.</param>
    /// <returns>A task.</returns>
    /// <exception cref="NotSupportedException">The provider does not implement it.</exception>
    protected virtual Task ApplyRelaxNotNullAsync(string defaultSchema, RelaxNotNullOperation op) =>
        throw new NotSupportedException(
            $"Migration operation '{nameof(RelaxNotNullOperation)}' is not supported by {GetType().Name}.");
}
