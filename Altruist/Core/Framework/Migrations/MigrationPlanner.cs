using Altruist.Persistence;

namespace Altruist.Migrations;

public sealed class DatabaseModel
{
    public string Schema { get; }
    public IReadOnlyDictionary<string, TableModel> Tables { get; }

    public DatabaseModel(string schema, IReadOnlyDictionary<string, TableModel> tables)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        Tables = tables ?? throw new ArgumentNullException(nameof(tables));
    }

    public bool TryGetTable(string tableName, out TableModel table) =>
        Tables.TryGetValue(tableName, out table!);
}

public sealed class SchemaModel
{
    public string Name { get; init; } = "";
    public IReadOnlyDictionary<string, TableModel> Tables { get; init; } =
        new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase);
}


public sealed class TableModel
{
    public string Name { get; }
    public IReadOnlyDictionary<string, ColumnModel> Columns { get; }
    public IReadOnlyList<string> PrimaryKeyColumns { get; }
    /// <summary>
    /// ColumnName -> constraintName
    /// </summary>
    public IReadOnlyDictionary<string, string> UniqueConstraints { get; }
    /// <summary>
    /// IndexName -> IndexModel
    /// </summary>
    public IReadOnlyDictionary<string, IndexModel> Indexes { get; }

    public TableModel(
        string name,
        IReadOnlyDictionary<string, ColumnModel> columns,
        IReadOnlyList<string> primaryKeyColumns,
        IReadOnlyDictionary<string, string> uniqueConstraints,
        IReadOnlyDictionary<string, IndexModel> indexes)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        PrimaryKeyColumns = primaryKeyColumns ?? Array.Empty<string>();
        UniqueConstraints = uniqueConstraints ?? new Dictionary<string, string>();
        Indexes = indexes ?? new Dictionary<string, IndexModel>();
    }
}

public sealed class ColumnModel
{
    public string Name { get; }
    public string StoreType { get; }
    public bool IsNullable { get; }

    public ColumnModel(string name, string storeType, bool isNullable)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        StoreType = storeType ?? throw new ArgumentNullException(nameof(storeType));
        IsNullable = isNullable;
    }
}

public sealed class IndexModel
{
    public string Name { get; }
    public string Column { get; }

    public IndexModel(string name, string column)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Column = column ?? throw new ArgumentNullException(nameof(column));
    }
}

public interface IMigrationPlanner
{
    IReadOnlyList<MigrationOperation> Plan(
        DatabaseModel current,
        IReadOnlyList<Document> desiredDocuments,
        string schemaName);
}
