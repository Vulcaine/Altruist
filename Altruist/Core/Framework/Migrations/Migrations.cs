namespace Altruist.Migrations;

public abstract record MigrationOperation;

public sealed record CreateTableOperation(
    string Schema,
    string Table,
    IReadOnlyList<ColumnDefinition> Columns,
    IReadOnlyList<string> PrimaryKeyColumns
) : MigrationOperation;

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

public sealed record ColumnDefinition(
    string Name,
    string StoreType,
    bool IsNullable,
    bool IsUnique
);

public sealed record AddForeignKeyOperation(
    string Schema,
    string Table,
    string ConstraintName,
    string Column,
    string PrincipalTable,
    string PrincipalColumn
) : MigrationOperation;

public sealed record DropForeignKeyOperation(
    string Schema,
    string Table,
    string ConstraintName
) : MigrationOperation;
