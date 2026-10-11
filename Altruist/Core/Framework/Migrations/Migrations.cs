namespace Altruist.Migrations;

/// <summary>
/// Base type of one schema change produced by <see cref="IMigrationPlanner"/> and applied, in order, by
/// <see cref="IMigrationExecutor"/>. Operations are plain data; providers translate them to DDL.
/// </summary>
public abstract record MigrationOperation;

// ----------------- schema-level -----------------

/// <summary>Creates a schema (keyspace) if missing.</summary>
/// <param name="Schema">Schema name.</param>
public sealed record CreateSchemaOperation(
    string Schema
) : MigrationOperation;

// (If you ever want DropSchema, you can add it similarly.)

// ----------------- table-level -----------------

/// <summary>Creates a table with its columns and primary key.</summary>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="Columns">Column definitions.</param>
/// <param name="PrimaryKeyColumns">Physical primary key columns, in order.</param>
public sealed record CreateTableOperation(
    string Schema,
    string Table,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<string> PrimaryKeyColumns
) : MigrationOperation;

/// <summary>
/// Archives a table by copying all data to an archive table, then dropping the original.
/// </summary>
/// <param name="Schema">Schema name.</param>
/// <param name="SourceTable">Table to archive.</param>
/// <param name="ArchiveTable">Archive table receiving the rows.</param>
public sealed record ArchiveTableOperation(
    string Schema,
    string SourceTable,
    string ArchiveTable
) : MigrationOperation;

/// <summary>Drops a table (destructive). Planned for <see cref="Altruist.UORM.VaultTableDeleteAttribute"/> / <see cref="Altruist.UORM.VaultArchivedAttribute"/> models.</summary>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
public sealed record DropTableOperation(
    string Schema,
    string Table
) : MigrationOperation;

// ----------------- column-level -----------------

/// <summary>Adds a column to an existing table.</summary>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="Column">The new column.</param>
public sealed record AddColumnOperation(
    string Schema,
    string Table,
    ColumnDefinition Column
) : MigrationOperation;

/// <summary>
/// Drops a column that exists in the database but is no longer mapped by the model (destructive: the data is lost).
/// </summary>
/// <remarks>
/// The built-in planner emits it only when dropping is opted into: globally with
/// <c>altruist:persistence:migration:drop-unmapped-columns: true</c>, or per column with
/// <see cref="Altruist.UORM.VaultDropColumnAttribute"/> / <see cref="Altruist.UORM.VaultColumnDeleteAttribute"/>
/// (those plan a <see cref="DeleteMarkedColumnOperation"/>). By default unmapped columns are kept (see
/// <see cref="RelaxNotNullOperation"/>).
/// </remarks>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="ColumnName">Column to drop.</param>
public sealed record DropColumnOperation(
    string Schema,
    string Table,
    string ColumnName
) : MigrationOperation;

/// <summary>
/// Drops the <c>NOT NULL</c> constraint of a column (non-destructive: no data changes).
/// </summary>
/// <remarks>
/// Planned for a column the model no longer maps that is <c>NOT NULL</c> without a default: the planner keeps such
/// columns (their data is preserved), but inserts from the model would no longer supply a value and would fail, so
/// the column is relaxed to allow NULL instead of being dropped. Providers implement it in
/// <see cref="AbstractMigrationExecutor.ApplyRelaxNotNullAsync"/>.
/// </remarks>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="ColumnName">Column whose NOT NULL constraint is dropped.</param>
public sealed record RelaxNotNullOperation(
    string Schema,
    string Table,
    string ColumnName
) : MigrationOperation;

/// <summary>Renames a column, preserving its data (planned from <see cref="Altruist.UORM.VaultRenamedFromAttribute"/>).</summary>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="OldColumnName">Current column name.</param>
/// <param name="NewColumnName">New column name.</param>
public sealed record RenameColumnOperation(
    string Schema,
    string Table,
    string OldColumnName,
    string NewColumnName
) : MigrationOperation;

/// <summary>
/// Copies data from one column to another with type conversion.
/// Executed as batched UPDATE with USING cast.
/// </summary>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="SourceColumn">Column to copy from.</param>
/// <param name="TargetColumn">Column to copy into.</param>
/// <param name="TargetStoreType">Store type the values are cast to.</param>
public sealed record CopyColumnDataOperation(
    string Schema,
    string Table,
    string SourceColumn,
    string TargetColumn,
    string TargetStoreType
) : MigrationOperation;

/// <summary>
/// Drops a column marked with [VaultColumnDelete] (on a property) or [VaultDropColumn] (on the class), or a
/// <see cref="Altruist.UORM.VaultColumnCopyAttribute"/> source column when unmapped columns are dropped globally.
/// Runs after CopyColumnData operations.
/// </summary>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="ColumnName">Column to drop.</param>
/// <param name="Reason">Reason from the attribute, for logs.</param>
public sealed record DeleteMarkedColumnOperation(
    string Schema,
    string Table,
    string ColumnName,
    string Reason
) : MigrationOperation;

/// <summary>Changes a column's store type (the provider converts existing data).</summary>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="ColumnName">Column to alter.</param>
/// <param name="OldStoreType">Current store type.</param>
/// <param name="NewStoreType">Desired store type.</param>
public sealed record AlterColumnTypeOperation(
    string Schema,
    string Table,
    string ColumnName,
    string OldStoreType,
    string NewStoreType
) : MigrationOperation;

// ----------------- constraints -----------------

/// <summary>Adds a UNIQUE constraint over one or more columns.</summary>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="ConstraintName">Constraint name (see <see cref="Altruist.Persistence.ConstraintUtil"/>).</param>
/// <param name="Columns">Physical columns.</param>
public sealed record AddUniqueConstraintOperation(
    string Schema,
    string Table,
    string ConstraintName,
    IReadOnlyList<string> Columns
) : MigrationOperation;

/// <summary>Drops a constraint by name.</summary>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="ConstraintName">Constraint to drop.</param>
public sealed record DropConstraintOperation(
    string Schema,
    string Table,
    string ConstraintName
) : MigrationOperation;

// ----------------- indexes -----------------

/// <summary>Creates a single-column index.</summary>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="IndexName">Index name.</param>
/// <param name="Column">Indexed column.</param>
public sealed record CreateIndexOperation(
    string Schema,
    string Table,
    string IndexName,
    string Column
) : MigrationOperation;

/// <summary>Drops an index by name.</summary>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="IndexName">Index to drop.</param>
public sealed record DropIndexOperation(
    string Schema,
    string Table,
    string IndexName
) : MigrationOperation;

// ----------------- foreign keys -----------------

/// <summary>Adds a foreign key constraint (planned from <see cref="Altruist.UORM.VaultForeignKeyAttribute"/>).</summary>
/// <param name="Schema">Dependent table schema.</param>
/// <param name="Table">Dependent table.</param>
/// <param name="ConstraintName">Constraint name.</param>
/// <param name="Column">Dependent column.</param>
/// <param name="PrincipalSchema">Referenced table schema.</param>
/// <param name="PrincipalTable">Referenced table.</param>
/// <param name="PrincipalColumn">Referenced column.</param>
/// <param name="OnDelete">ON DELETE action (see <see cref="Altruist.UORM.VaultForeignKeyDeleteBehavior"/>).</param>
public sealed record AddForeignKeyOperation(
    string Schema,
    string Table,
    string ConstraintName,
    string Column,
    string PrincipalSchema,
    string PrincipalTable,
    string PrincipalColumn,
    string OnDelete
) : MigrationOperation;

/// <summary>Drops a foreign key constraint by name.</summary>
/// <param name="Schema">Schema name.</param>
/// <param name="Table">Table name.</param>
/// <param name="ConstraintName">Constraint to drop.</param>
public sealed record DropForeignKeyOperation(
    string Schema,
    string Table,
    string ConstraintName
) : MigrationOperation;

// ----------------- support types -----------------

/// <summary>Column shape used by table/column creation operations.</summary>
/// <param name="Name">Physical column name.</param>
/// <param name="StoreType">Provider store type (e.g. <c>text</c>, <c>bigint</c>).</param>
/// <param name="IsNullable">Whether NULL is allowed.</param>
/// <param name="IsUnique">Whether the column gets an inline single-column UNIQUE constraint.</param>
/// <param name="DefaultSql">Optional SQL DEFAULT expression.</param>
public sealed record ColumnDefinition(
    string Name,
    string StoreType,
    bool IsNullable,
    bool IsUnique,
    string? DefaultSql = null
);
