namespace Altruist.Migrations;

public abstract record MigrationOperation;

// ----------------- schema-level -----------------

public sealed record CreateSchemaOperation(
    string Schema
) : MigrationOperation;

// (If you ever want DropSchema, you can add it similarly.)

// ----------------- table-level -----------------

public sealed record CreateTableOperation(
    string Schema,
    string Table,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<string> PrimaryKeyColumns
) : MigrationOperation;

public sealed record DropTableOperation(
    string Schema,
    string Table
) : MigrationOperation;

// ----------------- column-level -----------------

public sealed record AddColumnOperation(
    string Schema,
    string Table,
    ColumnDefinition Column
) : MigrationOperation;

public sealed record DropColumnOperation(
    string Schema,
    string Table,
    string ColumnName
) : MigrationOperation;

// ----------------- constraints -----------------

public sealed record AddUniqueConstraintOperation(
    string Schema,
    string Table,
    string ConstraintName,
    string Column
) : MigrationOperation;

public sealed record DropConstraintOperation(
    string Schema,
    string Table,
    string ConstraintName
) : MigrationOperation;

// ----------------- indexes -----------------

public sealed record CreateIndexOperation(
    string Schema,
    string Table,
    string IndexName,
    string Column
) : MigrationOperation;

public sealed record DropIndexOperation(
    string Schema,
    string Table,
    string IndexName
) : MigrationOperation;

// ----------------- foreign keys -----------------

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

public sealed record DropForeignKeyOperation(
    string Schema,
    string Table,
    string ConstraintName
) : MigrationOperation;

// ----------------- support types -----------------

public sealed record ColumnDefinition(
    string Name,
    string StoreType,
    bool IsNullable,
    bool IsUnique
);
