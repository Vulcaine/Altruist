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
    public IReadOnlyDictionary<string, string> UniqueConstraints { get; }
    public IReadOnlyDictionary<string, IndexModel> Indexes { get; }

    // NEW:
    public IReadOnlyList<ForeignKeyModel> ForeignKeys { get; }

    public TableModel(
        string name,
        IReadOnlyDictionary<string, ColumnModel> columns,
        IReadOnlyList<string> primaryKeyColumns,
        IReadOnlyDictionary<string, string> uniqueConstraints,
        IReadOnlyDictionary<string, IndexModel> indexes,
        IReadOnlyList<ForeignKeyModel> foreignKeys)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        PrimaryKeyColumns = primaryKeyColumns ?? Array.Empty<string>();
        UniqueConstraints = uniqueConstraints ?? new Dictionary<string, string>();
        Indexes = indexes ?? new Dictionary<string, IndexModel>();
        ForeignKeys = foreignKeys ?? Array.Empty<ForeignKeyModel>();
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

public sealed class ForeignKeyModel
{
    public string Name { get; }
    public string Column { get; }
    public string PrincipalTable { get; }
    public string PrincipalColumn { get; }

    public ForeignKeyModel(string name, string column, string principalTable, string principalColumn)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Column = column ?? throw new ArgumentNullException(nameof(column));
        PrincipalTable = principalTable ?? throw new ArgumentNullException(nameof(principalTable));
        PrincipalColumn = principalColumn ?? throw new ArgumentNullException(nameof(principalColumn));
    }
}

public interface IMigrationPlanner
{
    IReadOnlyList<MigrationOperation> Plan(
        DatabaseModel current,
        IReadOnlyList<Document> desiredDocuments,
        string schemaName);
}

public abstract class AbstractMigrationPlanner : IMigrationPlanner
{
    public IReadOnlyList<MigrationOperation> Plan(
    DatabaseModel current,
    IReadOnlyList<Document> desiredDocuments,
    string schemaName)
    {
        var ops = new List<MigrationOperation>();
        var schemaNormalized = NormalizeSchemaName(schemaName);

        // Only operate on docs whose Vault keyspace matches this schema.
        // We still keep the full desiredDocuments list for FK resolution (cross-schema).
        var docsForSchemaRaw = desiredDocuments
            .Where(d =>
            {
                var keyspace = d.Header.Keyspace ?? GetDefaultSchemaName();
                var docSchema = NormalizeSchemaName(keyspace);
                return string.Equals(docSchema, schemaNormalized, StringComparison.OrdinalIgnoreCase);
            })
            .ToList();

        // 🔽 NEW: sort by foreign-key dependencies so principals come before dependents
        var docsForSchema = SortDocumentsByDependencies(docsForSchemaRaw, desiredDocuments, schemaNormalized);

        // ─────────────────────────────────────────────
        // 1st pass: tables, columns, uniques, indexes, history
        // ─────────────────────────────────────────────
        foreach (var doc in docsForSchema)
        {
            var tableName = doc.Name;
            current.TryGetTable(tableName, out var existingTable);

            if (existingTable is null)
            {
                PlanNewTable(ops, schemaNormalized, doc, desiredDocuments);
                PlanHistoryTableForNew(ops, schemaNormalized, doc);
            }
            else
            {
                PlanExistingTableDiff(ops, schemaNormalized, doc, existingTable, desiredDocuments);
                PlanHistoryTableDiff(ops, schemaNormalized, doc, current);
            }
        }

        // ─────────────────────────────────────────────
        // 2nd pass: foreign keys (after all tables exist)
        // ─────────────────────────────────────────────
        foreach (var doc in docsForSchema)
        {
            var tableName = doc.Name;
            current.TryGetTable(tableName, out var existingTable);

            if (existingTable is null)
            {
                PlanForeignKeysForNewTable(ops, schemaNormalized, doc, desiredDocuments);
            }
            else
            {
                PlanForeignKeyDiff(ops, schemaNormalized, doc, existingTable, desiredDocuments);
            }
        }

        return ops;
    }

    /// <summary>
    /// Returns docs for this schema sorted so that any document A that is the principal
    /// of a foreign key from document B will appear before B (within the same schema).
    /// Detects and throws on circular dependencies.
    /// </summary>
    protected IReadOnlyList<Document> SortDocumentsByDependencies(
        IReadOnlyList<Document> docsForSchema,
        IReadOnlyList<Document> allDocs,
        string schemaNormalized)
    {
        if (docsForSchema.Count <= 1)
            return docsForSchema;

        // Map Type -> Document for quick lookup
        var docsByType = allDocs
            .GroupBy(d => d.Type)
            .ToDictionary(g => g.Key, g => g.First());

        var docsSet = new HashSet<Document>(docsForSchema);
        var adjacency = new Dictionary<Document, HashSet<Document>>(); // principal -> dependents
        var inDegree = new Dictionary<Document, int>();

        foreach (var doc in docsForSchema)
        {
            adjacency[doc] = new HashSet<Document>();
            inDegree[doc] = 0;
        }

        // Build dependency graph:
        // For each FK: principalDoc -> doc
        foreach (var doc in docsForSchema)
        {
            foreach (var fk in doc.ForeignKeys)
            {
                if (!docsByType.TryGetValue(fk.PrincipalType, out var principalDoc))
                {
                    // Principal type not in the known documents; the planner will
                    // complain separately if it's truly missing. Ignore for ordering.
                    continue;
                }

                // Only consider dependencies within the same schema; cross-schema
                // references will be handled by separate schema migrations.
                var keyspace = principalDoc.Header.Keyspace ?? GetDefaultSchemaName();
                var principalSchema = NormalizeSchemaName(keyspace);

                if (!string.Equals(principalSchema, schemaNormalized, StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!docsSet.Contains(principalDoc))
                    continue;

                // Edge: principalDoc -> doc
                if (adjacency[principalDoc].Add(doc))
                {
                    inDegree[doc] = inDegree[doc] + 1;
                }
            }
        }

        // Kahn's algorithm for topological sort
        var queue = new Queue<Document>(inDegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var sorted = new List<Document>(docsForSchema.Count);

        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            sorted.Add(node);

            foreach (var dependent in adjacency[node])
            {
                inDegree[dependent] = inDegree[dependent] - 1;
                if (inDegree[dependent] == 0)
                    queue.Enqueue(dependent);
            }
        }

        if (sorted.Count != docsForSchema.Count)
        {
            // Circular dependency detected among the remaining nodes
            var cyclicDocs = inDegree
                .Where(kv => kv.Value > 0)
                .Select(kv => kv.Key.Type.Name)
                .Distinct()
                .OrderBy(n => n)
                .ToArray();

            throw new InvalidOperationException(
                "Detected circular foreign-key dependency among vaults in schema '" + schemaNormalized + "': " +
                string.Join(", ", cyclicDocs));
        }

        return sorted;
    }

    // ---------- provider hooks ----------

    /// <summary>
    /// Provider-specific mapping from CLR type to database column store type.
    /// </summary>
    protected abstract string MapClrTypeToStoreType(Type type);

    /// <summary>
    /// Default schema name for this provider (e.g. "public", "dbo").
    /// </summary>
    protected virtual string GetDefaultSchemaName() => "public";

    /// <summary>
    /// How schema names are normalized for comparison / DDL.
    /// </summary>
    protected virtual string NormalizeSchemaName(string? schemaName)
    {
        var s = schemaName;
        if (string.IsNullOrWhiteSpace(s))
            s = GetDefaultSchemaName();

        return s.Trim().ToLowerInvariant();
    }

    /// <summary>
    /// Store type for the history table's "timestamp" column.
    /// </summary>
    protected virtual string HistoryTimestampStoreType => "timestamp";

    // ---------- core planning logic (provider-agnostic, uses hooks above) ----------

    protected void PlanNewTable(
        List<MigrationOperation> ops,
        string schema,
        Document doc,
        IReadOnlyList<Document> allDocs)
    {
        var pkCols = ResolvePrimaryKeyColumns(doc);
        if (pkCols.Count == 0)
            throw new InvalidOperationException($"PrimaryKeyAttribute is required on '{doc.Type.Name}'.");

        var pkSet = new HashSet<string>(pkCols, StringComparer.OrdinalIgnoreCase);
        var uniqueSet = new HashSet<string>(doc.UniqueKeys, StringComparer.OrdinalIgnoreCase);

        var columns = new List<ColumnDefinition>(doc.Columns.Count);

        foreach (var kv in doc.Columns)
        {
            var logicalName = kv.Key;     // C# property name
            var columnName = kv.Value;    // physical column name

            if (!doc.FieldTypes.TryGetValue(logicalName, out var clrType))
            {
                throw new InvalidOperationException(
                    $"Field type for '{logicalName}' not found on '{doc.Type.Name}'. " +
                    "Ensure Document.FieldTypes is populated.");
            }

            var storeType = MapClrTypeToStoreType(clrType);

            bool isPk = pkSet.Contains(columnName);
            bool isUnique = uniqueSet.Contains(columnName) || isPk;
            bool isNullable = !isPk && doc.NullableColumns.Contains(columnName);

            columns.Add(new ColumnDefinition(
                Name: columnName,
                StoreType: storeType,
                IsNullable: isNullable,
                IsUnique: isUnique));
        }

        ops.Add(new CreateTableOperation(
            Schema: schema,
            Table: doc.Name,
            Columns: columns,
            PrimaryKeyColumns: pkCols));

        var indexColumns = new HashSet<string>(
            doc.Indexes ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var sortCol = ResolveSortingColumn(doc);
        if (sortCol is not null)
            indexColumns.Add(sortCol);

        indexColumns.RemoveWhere(c => pkSet.Contains(c) || uniqueSet.Contains(c));

        foreach (var col in indexColumns)
        {
            var indexName = $"{doc.Name}_{col}_idx";
            ops.Add(new CreateIndexOperation(schema, doc.Name, indexName, col));
        }
    }

    protected void PlanForeignKeysForNewTable(
        List<MigrationOperation> ops,
        string schema,
        Document doc,
        IReadOnlyList<Document> allDocs)
    {
        foreach (var fk in doc.ForeignKeys)
        {
            var (principalTable, principalColumn) =
                ResolveForeignKeyTarget(doc, fk, allDocs);

            var constraintName =
                $"fk_{doc.Name}_{fk.ColumnName}_{principalTable}_{principalColumn}";

            ops.Add(new AddForeignKeyOperation(
                Schema: schema,
                Table: doc.Name,
                ConstraintName: constraintName,
                Column: fk.ColumnName,
                PrincipalTable: principalTable,
                PrincipalColumn: principalColumn,
                OnDelete: fk.OnDelete
            ));
        }
    }

    protected void PlanForeignKeyDiff(
        List<MigrationOperation> ops,
        string schema,
        Document doc,
        TableModel existing,
        IReadOnlyList<Document> allDocs)
    {
        var desired = new List<(string Column, string PrincipalTable, string PrincipalColumn, string ConstraintName, string OnDelete)>();

        foreach (var fk in doc.ForeignKeys)
        {
            var (principalTable, principalColumn) = ResolveForeignKeyTarget(doc, fk, allDocs);
            var constraintName = $"fk_{doc.Name}_{fk.ColumnName}_{principalTable}_{principalColumn}";

            desired.Add((fk.ColumnName, principalTable, principalColumn, constraintName, fk.OnDelete));
        }

        var existingFks = existing.ForeignKeys ?? Array.Empty<ForeignKeyModel>();

        // add missing
        foreach (var dfk in desired)
        {
            bool already = existingFks.Any(efk =>
                string.Equals(efk.Column, dfk.Column, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(efk.PrincipalTable, dfk.PrincipalTable, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(efk.PrincipalColumn, dfk.PrincipalColumn, StringComparison.OrdinalIgnoreCase));

            if (!already)
            {
                ops.Add(new AddForeignKeyOperation(
                    Schema: schema,
                    Table: doc.Name,
                    ConstraintName: dfk.ConstraintName,
                    Column: dfk.Column,
                    PrincipalTable: dfk.PrincipalTable,
                    PrincipalColumn: dfk.PrincipalColumn,
                    OnDelete: dfk.OnDelete));
            }
        }

        // drop extra
        foreach (var efk in existingFks)
        {
            bool stillDesired = desired.Any(dfk =>
                string.Equals(dfk.Column, efk.Column, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(dfk.PrincipalTable, efk.PrincipalTable, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(dfk.PrincipalColumn, efk.PrincipalColumn, StringComparison.OrdinalIgnoreCase));

            if (!stillDesired)
            {
                ops.Add(new DropForeignKeyOperation(
                    Schema: schema,
                    Table: doc.Name,
                    ConstraintName: efk.Name));
            }
        }
    }

    protected (string PrincipalTable, string PrincipalColumn) ResolveForeignKeyTarget(
        Document doc,
        Document.VaultForeignKeyDefinition fk,
        IReadOnlyList<Document> allDocs)
    {
        // We search across ALL documents (all keyspaces) so cross-schema FKs work.
        var principalDoc = allDocs.FirstOrDefault(d => d.Type == fk.PrincipalType)
            ?? throw new InvalidOperationException(
                $"Referenced vault type '{fk.PrincipalType.Name}' for '{doc.Type.Name}.{fk.PropertyName}' " +
                "not found among desired documents.");

        // Map principal property name -> physical column.
        if (!principalDoc.Columns.TryGetValue(fk.PrincipalPropertyName, out var principalColumn))
        {
            // Fallback to camelCase if explicit column mapping not found
            principalColumn = Document.ToCamelCase(fk.PrincipalPropertyName);
        }

        // principalDoc.Name is the physical table name; schema is handled by the caller.
        return (principalDoc.Name, principalColumn);
    }

    protected void PlanHistoryTableForNew(
        List<MigrationOperation> ops,
        string schema,
        Document doc)
    {
        if (!doc.StoreHistory)
            return;

        var pkCols = ResolvePrimaryKeyColumns(doc);
        if (pkCols.Count == 0)
            throw new InvalidOperationException($"PrimaryKeyAttribute is required on '{doc.Type.Name}' for history.");

        var historyTable = doc.Name + "_history";

        var histCols = new List<ColumnDefinition>(doc.Columns.Count + 1);

        foreach (var kv in doc.Columns)
        {
            var logicalName = kv.Key;
            var physicalName = kv.Value;

            if (!doc.FieldTypes.TryGetValue(logicalName, out var clrType))
            {
                throw new InvalidOperationException(
                    $"Field type for '{logicalName}' not found on '{doc.Type.Name}' (history).");
            }

            var storeType = MapClrTypeToStoreType(clrType);

            histCols.Add(new ColumnDefinition(
                Name: physicalName,
                StoreType: storeType,
                IsNullable: true,    // history columns usually nullable
                IsUnique: false));
        }

        histCols.Add(new ColumnDefinition(
            Name: "timestamp",
            StoreType: HistoryTimestampStoreType,
            IsNullable: false,
            IsUnique: false));

        var historyPk = pkCols.Concat(new[] { "timestamp" }).ToArray();

        ops.Add(new CreateTableOperation(
            Schema: schema,
            Table: historyTable,
            Columns: histCols,
            PrimaryKeyColumns: historyPk));

        foreach (var key in pkCols)
        {
            var indexName = $"{doc.Name}_history_{key}_idx";
            ops.Add(new CreateIndexOperation(schema, historyTable, indexName, key));
        }
    }

    protected void PlanExistingTableDiff(
        List<MigrationOperation> ops,
        string schema,
        Document doc,
        TableModel existing,
        IReadOnlyList<Document> allDocs)
    {
        var tableName = doc.Name;
        var schemaName = schema;

        var existingCols = new HashSet<string>(existing.Columns.Keys, StringComparer.OrdinalIgnoreCase);
        var desiredCols = new HashSet<string>(doc.Columns.Values, StringComparer.OrdinalIgnoreCase);

        // columns to add
        foreach (var col in desiredCols.Except(existingCols, StringComparer.OrdinalIgnoreCase))
        {
            var logical = doc.Columns.First(kv =>
                    string.Equals(kv.Value, col, StringComparison.OrdinalIgnoreCase))
                .Key;

            if (!doc.FieldTypes.TryGetValue(logical, out var clrType))
            {
                throw new InvalidOperationException(
                    $"Field type for '{logical}' not found on '{doc.Type.Name}' for column '{col}'.");
            }

            var storeType = MapClrTypeToStoreType(clrType);

            var pkSet = new HashSet<string>(existing.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);
            var uniqueSet = new HashSet<string>(doc.UniqueKeys, StringComparer.OrdinalIgnoreCase);

            bool isPk = pkSet.Contains(col);
            bool isUnique = uniqueSet.Contains(col) || isPk;

            // use Document.NullableColumns for new columns as well
            bool isNullable = !isPk && doc.NullableColumns.Contains(col);

            var def = new ColumnDefinition(
                Name: col,
                StoreType: storeType,
                IsNullable: isNullable,
                IsUnique: isUnique);

            ops.Add(new AddColumnOperation(schemaName, tableName, def));
        }

        // columns to drop
        foreach (var col in existingCols.Except(desiredCols, StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new DropColumnOperation(schemaName, tableName, col));
        }

        // unique constraints
        var pkSet2 = new HashSet<string>(existing.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);
        var desiredUq = new HashSet<string>(doc.UniqueKeys, StringComparer.OrdinalIgnoreCase);
        var existingUq = existing.UniqueConstraints; // column -> constraint

        // add missing uniques
        foreach (var col in desiredUq)
        {
            if (pkSet2.Contains(col))
                continue;
            if (existingUq.ContainsKey(col))
                continue;

            var constraintName = $"uq_{doc.Name}_{col}";
            ops.Add(new AddUniqueConstraintOperation(schemaName, tableName, constraintName, col));
        }

        // drop uniques no longer desired
        foreach (var kv in existingUq)
        {
            var col = kv.Key;
            var constraintName = kv.Value;

            if (!desiredUq.Contains(col))
            {
                ops.Add(new DropConstraintOperation(schemaName, tableName, constraintName));
            }
        }

        // indexes
        var desiredIndexCols = new HashSet<string>(
            doc.Indexes ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var sortCol = ResolveSortingColumn(doc);
        if (sortCol is not null)
            desiredIndexCols.Add(sortCol);

        desiredIndexCols.RemoveWhere(c =>
            pkSet2.Contains(c) ||
            desiredUq.Contains(c));

        var existingIndexes = existing.Indexes.Values;

        // add missing
        foreach (var col in desiredIndexCols)
        {
            var expectedName = $"{doc.Name}_{col}_idx";

            bool already = existingIndexes.Any(ix =>
                string.Equals(ix.Name, expectedName, StringComparison.OrdinalIgnoreCase));

            if (!already)
            {
                ops.Add(new CreateIndexOperation(schemaName, tableName, expectedName, col));
            }
        }

        // drop indexes no longer desired
        foreach (var ix in existingIndexes)
        {
            if (!ix.Name.StartsWith(doc.Name + "_", StringComparison.OrdinalIgnoreCase) ||
                !ix.Name.EndsWith("_idx", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!desiredIndexCols.Contains(ix.Column))
            {
                ops.Add(new DropIndexOperation(schemaName, tableName, ix.Name));
            }
        }
    }

    protected void PlanHistoryTableDiff(
        List<MigrationOperation> ops,
        string schema,
        Document doc,
        DatabaseModel current)
    {
        if (!doc.StoreHistory)
            return;

        var historyTable = doc.Name + "_history";

        if (!current.TryGetTable(historyTable, out var existingHist))
        {
            // no history -> same as new
            PlanHistoryTableForNew(ops, schema, doc);
            return;
        }

        var desiredCols = new HashSet<string>(
            doc.Columns.Values.Append("timestamp"),
            StringComparer.OrdinalIgnoreCase);

        var existingCols = new HashSet<string>(
            existingHist.Columns.Keys,
            StringComparer.OrdinalIgnoreCase);

        // add columns
        foreach (var col in desiredCols.Except(existingCols, StringComparer.OrdinalIgnoreCase))
        {
            string storeType;

            if (string.Equals(col, "timestamp", StringComparison.OrdinalIgnoreCase))
            {
                storeType = HistoryTimestampStoreType;
            }
            else
            {
                var logical = doc.Columns.First(kv =>
                        string.Equals(kv.Value, col, StringComparison.OrdinalIgnoreCase))
                    .Key;

                if (!doc.FieldTypes.TryGetValue(logical, out var clrType))
                {
                    throw new InvalidOperationException(
                        $"Field type for '{logical}' not found on '{doc.Type.Name}' " +
                        $"for history column '{col}'.");
                }

                storeType = MapClrTypeToStoreType(clrType);
            }

            var def = new ColumnDefinition(
                Name: col,
                StoreType: storeType,
                IsNullable: true,
                IsUnique: false);

            ops.Add(new AddColumnOperation(schema, historyTable, def));
        }

        // drop columns
        foreach (var col in existingCols.Except(desiredCols, StringComparer.OrdinalIgnoreCase))
        {
            ops.Add(new DropColumnOperation(schema, historyTable, col));
        }

        // ensure indexes on pk columns
        var pkCols = ResolvePrimaryKeyColumns(doc);
        var existingIndexes = existingHist.Indexes.Values;

        foreach (var key in pkCols)
        {
            var expectedName = $"{doc.Name}_history_{key}_idx";
            bool already = existingIndexes.Any(ix =>
                string.Equals(ix.Name, expectedName, StringComparison.OrdinalIgnoreCase));

            if (!already)
            {
                ops.Add(new CreateIndexOperation(schema, historyTable, expectedName, key));
            }
        }
    }

    // ---------- helpers ----------

    protected static List<string> ResolvePrimaryKeyColumns(Document doc)
    {
        var result = new List<string>();
        var keys = doc.PrimaryKey?.Keys ?? Array.Empty<string>();
        foreach (var keyProp in keys)
        {
            if (doc.Columns.TryGetValue(keyProp, out var col))
                result.Add(col);
            else
                result.Add(Document.ToCamelCase(keyProp));
        }
        return result;
    }

    protected static string? ResolveSortingColumn(Document doc)
    {
        var sortProp = doc.SortingBy?.Name;
        if (string.IsNullOrWhiteSpace(sortProp))
            return null;

        return doc.Columns.TryGetValue(sortProp, out var col)
            ? col
            : Document.ToCamelCase(sortProp);
    }
}
