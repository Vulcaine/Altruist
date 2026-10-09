/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Security.Cryptography;
using System.Text;

using Altruist.Persistence;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Migrations;

/// <summary>Current physical state of one schema as read by <see cref="ISchemaInspector"/>: its tables keyed by name (case-insensitive).</summary>
public sealed class DatabaseModel
{
    /// <summary>Normalized schema name.</summary>
    public string Schema { get; }
    /// <summary>Tables keyed by name.</summary>
    public IReadOnlyDictionary<string, TableModel> Tables { get; }

    /// <summary>Creates the model.</summary>
    /// <param name="schema">Schema name.</param>
    /// <param name="tables">Tables keyed by name.</param>
    public DatabaseModel(string schema, IReadOnlyDictionary<string, TableModel> tables)
    {
        Schema = schema ?? throw new ArgumentNullException(nameof(schema));
        Tables = tables ?? throw new ArgumentNullException(nameof(tables));
    }

    /// <summary>Looks up a table by name.</summary>
    /// <param name="tableName">Table name.</param>
    /// <param name="table">The table when found.</param>
    /// <returns>True when the table exists.</returns>
    public bool TryGetTable(string tableName, out TableModel table) =>
        Tables.TryGetValue(tableName, out table!);
}

/// <summary>Schema name plus tables. Not used by the built-in planner, which works with <see cref="DatabaseModel"/>.</summary>
public sealed class SchemaModel
{
    /// <summary>Schema name.</summary>
    public string Name { get; init; } = "";
    /// <summary>Tables keyed by name (case-insensitive by default).</summary>
    public IReadOnlyDictionary<string, TableModel> Tables { get; init; } =
        new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Physical state of one table: columns, primary key, unique constraints, indexes and foreign keys.</summary>
public sealed class TableModel
{
    /// <summary>Table name.</summary>
    public string Name { get; }
    /// <summary>Columns keyed by name.</summary>
    public IReadOnlyDictionary<string, ColumnModel> Columns { get; }
    /// <summary>Primary key columns in order; empty when none.</summary>
    public IReadOnlyList<string> PrimaryKeyColumns { get; }

    /// <summary>
    /// Unique constraints on this table, keyed by constraint name.
    /// Each constraint can span one or more columns.
    /// </summary>
    public IReadOnlyDictionary<string, UniqueConstraintModel> UniqueConstraints { get; }

    /// <summary>Single-column indexes keyed by index name.</summary>
    public IReadOnlyDictionary<string, IndexModel> Indexes { get; }

    // NEW:
    /// <summary>Foreign keys declared on the table.</summary>
    public IReadOnlyList<ForeignKeyModel> ForeignKeys { get; }

    /// <summary>Creates the model; null collections become empty.</summary>
    /// <param name="name">Table name.</param>
    /// <param name="columns">Columns keyed by name.</param>
    /// <param name="primaryKeyColumns">Primary key columns.</param>
    /// <param name="uniqueConstraints">Unique constraints keyed by name.</param>
    /// <param name="indexes">Indexes keyed by name.</param>
    /// <param name="foreignKeys">Foreign keys.</param>
    /// <exception cref="ArgumentNullException"><paramref name="name"/> or <paramref name="columns"/> is null.</exception>
    public TableModel(
        string name,
        IReadOnlyDictionary<string, ColumnModel> columns,
        IReadOnlyList<string> primaryKeyColumns,
        IReadOnlyDictionary<string, UniqueConstraintModel> uniqueConstraints,
        IReadOnlyDictionary<string, IndexModel> indexes,
        IReadOnlyList<ForeignKeyModel> foreignKeys)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Columns = columns ?? throw new ArgumentNullException(nameof(columns));
        PrimaryKeyColumns = primaryKeyColumns ?? Array.Empty<string>();
        UniqueConstraints = uniqueConstraints ?? new Dictionary<string, UniqueConstraintModel>();
        Indexes = indexes ?? new Dictionary<string, IndexModel>();
        ForeignKeys = foreignKeys ?? Array.Empty<ForeignKeyModel>();
    }
}

/// <summary>Physical state of one column.</summary>
public sealed class ColumnModel
{
    /// <summary>Column name.</summary>
    public string Name { get; }
    /// <summary>Database store type as reported by the catalog.</summary>
    public string StoreType { get; }
    /// <summary>Whether the column allows NULL.</summary>
    public bool IsNullable { get; }

    /// <summary>
    /// Whether the database fills the column when an insert omits it (a <c>DEFAULT</c>, an identity or a generated
    /// column). The planner uses it to decide whether a column the model no longer maps would break inserts:
    /// a <c>NOT NULL</c> column without a default is relaxed to allow NULL (<see cref="RelaxNotNullOperation"/>).
    /// Inspectors that do not report it leave it <c>false</c>, which errs on the side of relaxing.
    /// </summary>
    public bool HasDefault { get; }

    /// <summary>Creates the model (no default reported).</summary>
    /// <param name="name">Column name.</param>
    /// <param name="storeType">Store type.</param>
    /// <param name="isNullable">Whether NULL is allowed.</param>
    public ColumnModel(string name, string storeType, bool isNullable)
        : this(name, storeType, isNullable, hasDefault: false)
    {
    }

    /// <summary>Creates the model. Use this overload from an inspector that can read column defaults.</summary>
    /// <param name="name">Column name.</param>
    /// <param name="storeType">Store type.</param>
    /// <param name="isNullable">Whether NULL is allowed.</param>
    /// <param name="hasDefault">Whether the database supplies a value when an insert omits the column (see <see cref="HasDefault"/>).</param>
    public ColumnModel(string name, string storeType, bool isNullable, bool hasDefault)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        StoreType = storeType ?? throw new ArgumentNullException(nameof(storeType));
        IsNullable = isNullable;
        HasDefault = hasDefault;
    }
}

/// <summary>A single-column index.</summary>
public sealed class IndexModel
{
    /// <summary>Index name.</summary>
    public string Name { get; }
    /// <summary>Indexed column.</summary>
    public string Column { get; }

    /// <summary>Creates the model.</summary>
    /// <param name="name">Index name.</param>
    /// <param name="column">Indexed column.</param>
    public IndexModel(string name, string column)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Column = column ?? throw new ArgumentNullException(nameof(column));
    }
}

/// <summary>A UNIQUE constraint over one or more columns.</summary>
public sealed class UniqueConstraintModel
{
    /// <summary>Constraint name.</summary>
    public string Name { get; }

    /// <summary>
    /// Physical column names participating in this UNIQUE constraint (in DB order).
    /// </summary>
    public List<string> Columns { get; }

    /// <summary>Creates the model.</summary>
    /// <param name="name">Constraint name.</param>
    /// <param name="columns">Columns, at least one.</param>
    /// <exception cref="ArgumentException"><paramref name="columns"/> is empty.</exception>
    public UniqueConstraintModel(string name, IEnumerable<string> columns)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Columns = new List<string>(columns ?? Array.Empty<string>());

        if (Columns.Count == 0)
        {
            throw new ArgumentException("Unique constraint must contain at least one column.", nameof(columns));
        }
    }
}

/// <summary>A foreign key constraint from one column to a principal table column.</summary>
public sealed class ForeignKeyModel
{
    /// <summary>Constraint name.</summary>
    public string Name { get; }
    /// <summary>Dependent column.</summary>
    public string Column { get; }
    /// <summary>Referenced table.</summary>
    public string PrincipalTable { get; }
    /// <summary>Referenced column.</summary>
    public string PrincipalColumn { get; }

    /// <summary>Creates the model.</summary>
    /// <param name="name">Constraint name.</param>
    /// <param name="column">Dependent column.</param>
    /// <param name="principalTable">Referenced table.</param>
    /// <param name="principalColumn">Referenced column.</param>
    public ForeignKeyModel(string name, string column, string principalTable, string principalColumn)
    {
        Name = name ?? throw new ArgumentNullException(nameof(name));
        Column = column ?? throw new ArgumentNullException(nameof(column));
        PrincipalTable = principalTable ?? throw new ArgumentNullException(nameof(principalTable));
        PrincipalColumn = principalColumn ?? throw new ArgumentNullException(nameof(principalColumn));
    }
}

/// <summary>
/// Planner now takes:
/// - all current schemas (DatabaseModel per schema),
/// - all desired Documents (already ordered by dependency).
/// Each Document knows its own schema (via VaultAttribute.Keyspace).
/// </summary>
public interface IMigrationPlanner
{
    /// <summary>Computes the operations that turn the current schemas into the desired documents.</summary>
    /// <param name="currentBySchema">Current state per normalized schema name; missing schemas are treated as empty.</param>
    /// <param name="desiredDocuments">Desired tables, ordered so foreign-key principals come first.</param>
    /// <returns>Operations in execution order; empty when nothing changes.</returns>
    IReadOnlyList<MigrationOperation> Plan(
        IReadOnlyDictionary<string, DatabaseModel> currentBySchema,
        IReadOnlyList<VaultDocument> desiredDocuments);
}

/// <summary>
/// Provider-agnostic diff engine. Providers supply type mapping (<see cref="MapClrTypeToStoreType"/>) and defaults;
/// the base plans in four passes over the documents.
/// </summary>
/// <remarks>
/// <list type="number">
/// <item>Tables: archive/drop tables marked <see cref="Altruist.UORM.VaultArchivedAttribute"/> /
/// <see cref="Altruist.UORM.VaultTableDeleteAttribute"/>; create new tables (with history tables when
/// <c>StoreHistory</c> is set); diff existing ones (renames from <see cref="Altruist.UORM.VaultRenamedFromAttribute"/>,
/// type changes, added columns, unmapped columns, unique constraints, indexes, history table).</item>
/// <item>Column copies from <see cref="Altruist.UORM.VaultColumnCopyAttribute"/> (only when the source column exists).</item>
/// <item>Drops of columns marked <see cref="Altruist.UORM.VaultColumnDeleteAttribute"/> /
/// <see cref="Altruist.UORM.VaultDropColumnAttribute"/> (only when they exist), and, when
/// <see cref="DropUnmappedColumns"/> is on, of unmapped copy-source columns (after their copy ran).</item>
/// <item>Foreign keys (add missing, drop stale), after all tables exist.</item>
/// </list>
/// <para><b>Unmapped columns are kept by default.</b> A column that exists in the database but is no longer mapped by
/// the model (removed or renamed without <see cref="Altruist.UORM.VaultRenamedFromAttribute"/>) is not dropped: a
/// warning lists it, and if it is <c>NOT NULL</c> without a default it is relaxed to allow NULL
/// (<see cref="RelaxNotNullOperation"/>) so inserts from the model keep working. Drop columns explicitly with
/// <see cref="Altruist.UORM.VaultDropColumnAttribute"/> on the class (or <see cref="Altruist.UORM.VaultColumnDeleteAttribute"/>
/// on a kept property), or for every unmapped column with <c>altruist:persistence:migration:drop-unmapped-columns: true</c>
/// (<see cref="DropUnmappedColumns"/>).</para>
/// <para>Existing column types are compared through <see cref="StoreTypesMatch"/>, so catalog spellings
/// (e.g. <c>timestamp without time zone</c>) do not plan a type change for an unchanged model.</para>
/// </remarks>
public abstract class AbstractMigrationPlanner : IMigrationPlanner
{
    /// <summary>Maximum generated constraint name length (kept in sync with <see cref="ConstraintUtil.MaxConstraintNameLength"/>).</summary>
    protected const int MaxConstraintNameLength = 60;

    /// <summary>Config key that turns on dropping of every unmapped column (see <see cref="DropUnmappedColumns"/>).</summary>
    public const string DropUnmappedColumnsConfigKey = "altruist:persistence:migration:drop-unmapped-columns";

    /// <summary>Logger for planning warnings (unmapped columns kept or relaxed).</summary>
    protected readonly ILogger Logger;

    /// <summary>
    /// When <c>true</c>, every database column the model no longer maps is dropped (<see cref="DropColumnOperation"/>,
    /// data lost) — the behaviour up to 0.9.9-beta. Default <c>false</c>: unmapped columns are kept, listed in a warning and,
    /// when they are <c>NOT NULL</c> without a default, relaxed to allow NULL. Bound from
    /// <c>altruist:persistence:migration:drop-unmapped-columns</c> by provider planners. Prefer per-column opt-in with
    /// <see cref="Altruist.UORM.VaultDropColumnAttribute"/>; turn this on only when the database is owned exclusively by
    /// the models and every removal is intentional.
    /// </summary>
    public bool DropUnmappedColumns { get; }

    /// <summary>Creates a planner that keeps unmapped columns and logs nowhere.</summary>
    protected AbstractMigrationPlanner()
        : this(dropUnmappedColumns: false, logger: null)
    {
    }

    /// <summary>Creates the planner.</summary>
    /// <param name="dropUnmappedColumns">See <see cref="DropUnmappedColumns"/>; <c>false</c> keeps unmapped columns.</param>
    /// <param name="logger">Receives the unmapped-column warnings; null discards them.</param>
    protected AbstractMigrationPlanner(bool dropUnmappedColumns, ILogger? logger)
    {
        DropUnmappedColumns = dropUnmappedColumns;
        Logger = logger ?? NullLogger.Instance;
    }

    /// <inheritdoc/>
    public IReadOnlyList<MigrationOperation> Plan(
        IReadOnlyDictionary<string, DatabaseModel> currentBySchema,
        IReadOnlyList<VaultDocument> desiredDocuments)
    {
        var ops = new List<MigrationOperation>();

        // ─────────────────────────────────────────────
        // 1st pass: tables, columns, uniques, indexes, history
        // ─────────────────────────────────────────────
        foreach (var doc in desiredDocuments)
        {
            var schema = GetSchemaForDocument(doc);

            if (!currentBySchema.TryGetValue(schema, out var current))
            {
                current = new DatabaseModel(
                    schema,
                    new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase));
            }

            // [VaultArchived] — copy data to archive table, then drop original
            if (doc.IsTableArchived)
            {
                current.TryGetTable(doc.Name, out var tableToArchive);
                if (tableToArchive != null)
                {
                    ops.Add(new ArchiveTableOperation(schema, doc.Name, doc.ArchiveTableName));
                    current.TryGetTable(doc.Name + "_history", out var historyToArchive);
                    if (historyToArchive != null)
                        ops.Add(new DropTableOperation(schema, doc.Name + "_history"));
                    ops.Add(new DropTableOperation(schema, doc.Name));
                }
                continue;
            }

            // [VaultTableDelete] — drop entire table if it exists in DB
            if (doc.IsTableDeleted)
            {
                current.TryGetTable(doc.Name, out var tableToDelete);
                if (tableToDelete != null)
                {
                    current.TryGetTable(doc.Name + "_history", out var historyToDelete);
                    if (historyToDelete != null)
                        ops.Add(new DropTableOperation(schema, doc.Name + "_history"));
                    ops.Add(new DropTableOperation(schema, doc.Name));
                }
                continue;
            }

            current.TryGetTable(doc.Name, out var existingTable);

            if (existingTable is null)
            {
                PlanNewTable(ops, schema, doc, desiredDocuments);
                PlanHistoryTableForNew(ops, schema, doc);
            }
            else
            {
                PlanExistingTableDiff(ops, schema, doc, existingTable, desiredDocuments);
                PlanHistoryTableDiff(ops, schema, doc, current);
            }
        }

        // ─────────────────────────────────────────────
        // 2nd pass: [VaultColumnCopy] — copy data between columns (before deletes)
        // ─────────────────────────────────────────────
        foreach (var doc in desiredDocuments)
        {
            if (doc.CopyFromColumns.Count == 0) continue;
            var schema = GetSchemaForDocument(doc);

            if (!currentBySchema.TryGetValue(schema, out var current))
                current = new DatabaseModel(schema, new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase));

            current.TryGetTable(doc.Name, out var existingTable);

            foreach (var (targetCol, sourceCol) in doc.CopyFromColumns)
            {
                // Only emit if source exists in DB (otherwise nothing to copy)
                if (existingTable?.Columns.ContainsKey(sourceCol) == true)
                {
                    var targetType = "text"; // default
                    var logical = doc.Columns.FirstOrDefault(kv =>
                        string.Equals(kv.Value, targetCol, StringComparison.OrdinalIgnoreCase)).Key;
                    if (logical != null && doc.FieldTypes.TryGetValue(logical, out var clrType))
                        targetType = MapClrTypeToStoreType(clrType);

                    ops.Add(new CopyColumnDataOperation(schema, doc.Name, sourceCol, targetCol, targetType));
                }
            }
        }

        // ─────────────────────────────────────────────
        // 3rd pass: [VaultColumnDelete] — drop marked columns (after copies complete)
        // ─────────────────────────────────────────────
        foreach (var doc in desiredDocuments)
        {
            if (doc.IsTableArchived || doc.IsTableDeleted) continue;
            if (doc.DeletedColumns.Count == 0 && (!DropUnmappedColumns || doc.CopyFromColumns.Count == 0)) continue;
            var schema = GetSchemaForDocument(doc);

            if (!currentBySchema.TryGetValue(schema, out var current))
                current = new DatabaseModel(schema, new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase));

            current.TryGetTable(doc.Name, out var existingTable);

            foreach (var (colName, reason) in doc.DeletedColumns)
            {
                // Only drop if column actually exists in DB
                if (existingTable?.Columns.ContainsKey(colName) == true)
                {
                    ops.Add(new DeleteMarkedColumnOperation(schema, doc.Name, colName, reason));
                }
            }

            // Unmapped copy sources are skipped by the 1st pass (the copy needs them); with global
            // dropping they go here, after the copy ran.
            if (DropUnmappedColumns && existingTable is not null)
            {
                var mapped = new HashSet<string>(doc.Columns.Values, StringComparer.OrdinalIgnoreCase);
                foreach (var source in doc.CopyFromColumns.Values.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    if (mapped.Contains(source) || doc.DeletedColumns.ContainsKey(source)) continue;
                    if (!existingTable.Columns.ContainsKey(source)) continue;
                    ops.Add(new DeleteMarkedColumnOperation(schema, doc.Name, source,
                        $"unmapped copy source ({DropUnmappedColumnsConfigKey})"));
                }
            }
        }

        // ─────────────────────────────────────────────
        // 4th pass: foreign keys (after all tables exist)
        // ─────────────────────────────────────────────
        foreach (var doc in desiredDocuments)
        {
            var schema = GetSchemaForDocument(doc);

            if (!currentBySchema.TryGetValue(schema, out var current))
            {
                current = new DatabaseModel(
                    schema,
                    new Dictionary<string, TableModel>(StringComparer.OrdinalIgnoreCase));
            }

            current.TryGetTable(doc.Name, out var existingTable);

            if (existingTable is null)
            {
                // table is new in this migration
                PlanForeignKeysForNewTable(ops, schema, doc, desiredDocuments);
            }
            else
            {
                PlanForeignKeyDiff(ops, schema, doc, existingTable, desiredDocuments);
            }
        }

        return ops;
    }

    // ---------- provider hooks ----------

    /// <summary>
    /// Provider-specific mapping from CLR type to database column store type.
    /// </summary>
    protected abstract string MapClrTypeToStoreType(Type type);

    /// <summary>
    /// Provider-specific mapping from a model initializer/default value to a database DEFAULT expression.
    /// </summary>
    protected virtual string? MapClrDefaultValueToStoreDefault(object? value, Type type) => null;

    /// <summary>DEFAULT expression for a column, from the model's initializer value via <see cref="MapClrDefaultValueToStoreDefault"/>; null when none.</summary>
    /// <param name="doc">The document.</param>
    /// <param name="columnName">Physical column name.</param>
    /// <param name="clrType">Property type.</param>
    /// <returns>SQL DEFAULT expression or null.</returns>
    protected string? ResolveColumnDefaultSql(VaultDocument doc, string columnName, Type clrType)
    {
        return doc.ColumnDefaultValues.TryGetValue(columnName, out var defaultValue)
            ? MapClrDefaultValueToStoreDefault(defaultValue, clrType)
            : null;
    }

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

    /// <summary>
    /// Canonical spelling of a store type used for comparisons (default: trimmed, lower-case). Providers override it
    /// to fold catalog aliases onto the names <see cref="MapClrTypeToStoreType"/> returns (Postgres:
    /// <c>timestamp without time zone</c> → <c>timestamp</c>, <c>int4</c> → <c>integer</c>, <c>_text</c> → <c>text[]</c>).
    /// Override when adding a provider whose inspector reports types differently from its type map; otherwise an
    /// unchanged model plans an <see cref="AlterColumnTypeOperation"/> on every startup.
    /// </summary>
    /// <param name="storeType">Store type as reported by the inspector or produced by the type map.</param>
    /// <returns>The canonical spelling.</returns>
    protected virtual string NormalizeStoreType(string storeType) =>
        (storeType ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>
    /// Whether an existing column's store type already satisfies the desired one, i.e. no
    /// <see cref="AlterColumnTypeOperation"/> is needed. Default: equal after <see cref="NormalizeStoreType"/>.
    /// Override only for catalog types whose exact spelling is unknowable (e.g. a bare <c>ARRAY</c>).
    /// </summary>
    /// <param name="existingStoreType">Type reported by the inspector.</param>
    /// <param name="desiredStoreType">Type from <see cref="MapClrTypeToStoreType"/>.</param>
    /// <returns><c>true</c> when the types match.</returns>
    protected virtual bool StoreTypesMatch(string existingStoreType, string desiredStoreType) =>
        string.Equals(NormalizeStoreType(existingStoreType), NormalizeStoreType(desiredStoreType), StringComparison.Ordinal);

    /// <summary>
    /// Computes the schema name for a Document from its [Vault(Keyspace = ...)] header.
    /// </summary>
    protected string GetSchemaForDocument(VaultDocument d)
    {
        var keyspace = d.Header.Keyspace;
        if (string.IsNullOrWhiteSpace(keyspace))
            return NormalizeSchemaName(GetDefaultSchemaName());

        return NormalizeSchemaName(keyspace);
    }

    // ---------- helpers for unique constraints ----------

    /// <summary>Order- and case-insensitive key for a column set (lower-cased, sorted, joined with <c>|</c>); used to match unique constraints.</summary>
    /// <param name="columns">Column names.</param>
    /// <returns>The normalized key.</returns>
    protected static string NormalizeColumnSet(IEnumerable<string> columns)
    {
        return string.Join("|",
            columns
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.ToLowerInvariant())
                .OrderBy(c => c, StringComparer.Ordinal));
    }

    /// <summary>Name for a composite unique constraint: <c>uq_{table}_{cols}</c>, hashed when too long.</summary>
    /// <param name="tableName">Table name.</param>
    /// <param name="columns">Physical columns.</param>
    /// <returns>Constraint name.</returns>
    protected static string BuildUniqueConstraintName(string tableName, IReadOnlyList<string> columns)
    {
        // Deterministic, safe-length naming.
        // Example: uq_character_inventory_slot_kind
        return ConstructConstraintName("uq", tableName, string.Join("_", columns));
    }

    // ---------- core planning logic (provider-agnostic, uses hooks above) ----------

    /// <summary>Plans CREATE TABLE (single-column unique keys inline), composite unique constraints and indexes for a new table.</summary>
    /// <param name="ops">Operation list to append to.</param>
    /// <param name="schema">Target schema.</param>
    /// <param name="doc">The table's document.</param>
    /// <param name="allDocs">All desired documents.</param>
    /// <exception cref="InvalidOperationException">The model has no primary key or a field type is missing.</exception>
    protected void PlanNewTable(
        List<MigrationOperation> ops,
        string schema,
        VaultDocument doc,
        IReadOnlyList<VaultDocument> allDocs)
    {
        var pkCols = ResolvePrimaryKeyColumns(doc);
        if (pkCols.Count == 0)
            throw new InvalidOperationException($"PrimaryKeyAttribute is required on '{doc.Type.Name}'.");

        var pkSet = new HashSet<string>(pkCols, StringComparer.OrdinalIgnoreCase);

        var singleUniqueCols = new HashSet<string>(
            doc.UniqueKeys
               .Where(uk => uk.Columns.Count == 1)
               .Select(uk => uk.Columns[0]),
            StringComparer.OrdinalIgnoreCase);

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
            var defaultSql = ResolveColumnDefaultSql(doc, columnName, clrType);

            bool isPk = pkSet.Contains(columnName);
            bool isSingleUnique = singleUniqueCols.Contains(columnName);
            bool isNullable = !isPk && doc.NullableColumns.Contains(columnName);

            columns.Add(new ColumnDefinition(
                Name: columnName,
                StoreType: storeType,
                IsNullable: isNullable,
                IsUnique: isSingleUnique,
                DefaultSql: defaultSql)); // column-level UNIQUE only for single-col unique keys
        }

        ops.Add(new CreateTableOperation(
            Schema: schema,
            Table: doc.Name,
            Columns: columns,
            PrimaryKeyColumns: pkCols));

        // For new tables:
        // - Single-column unique constraints are already enforced via "UNIQUE" on the column.
        // - Composite unique constraints MUST be added via explicit ADD CONSTRAINT UNIQUE.
        foreach (var uk in doc.UniqueKeys.Where(uk => uk.Columns.Count > 1))
        {
            var constraintName = BuildUniqueConstraintName(doc.Name, uk.Columns);
            ops.Add(new AddUniqueConstraintOperation(
                schema,
                doc.Name,
                constraintName,
                uk.Columns.ToArray()));
        }

        var indexColumns = new HashSet<string>(
            doc.Indexes ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var sortCol = ResolveSortingColumn(doc);
        if (sortCol is not null)
            indexColumns.Add(sortCol);

        // Do not create separate indexes:
        // - on PK columns (implicit index)
        // - on single-column UNIQUE constraints (also implicit index)
        indexColumns.RemoveWhere(c =>
            pkSet.Contains(c) ||
            singleUniqueCols.Contains(c));

        foreach (var col in indexColumns)
        {
            var indexName = $"{doc.Name}_{col}_idx";
            ops.Add(new CreateIndexOperation(schema, doc.Name, indexName, col));
        }
    }

    /// <summary>Plans ADD FOREIGN KEY for every declared foreign key of a new table.</summary>
    /// <param name="ops">Operation list to append to.</param>
    /// <param name="schema">Table schema.</param>
    /// <param name="doc">The table's document.</param>
    /// <param name="allDocs">All desired documents (to resolve principals).</param>
    protected void PlanForeignKeysForNewTable(
    List<MigrationOperation> ops,
    string schema,
    VaultDocument doc,
    IReadOnlyList<VaultDocument> allDocs)
    {
        foreach (var fk in doc.ForeignKeys)
        {
            var (principalSchema, principalTable, principalColumn) =
                ResolveForeignKeyTarget(doc, fk, allDocs);

            // Unified, <= 60 chars, deterministic + unique
            var constraintName = ConstructConstraintName(
                "fk",
                doc.Name,
                fk.ColumnName,
                principalTable,
                principalColumn);

            ops.Add(new AddForeignKeyOperation(
                Schema: schema,               // dependent schema
                Table: doc.Name,              // dependent table
                ConstraintName: constraintName,
                Column: fk.ColumnName,
                PrincipalSchema: principalSchema,
                PrincipalTable: principalTable,
                PrincipalColumn: principalColumn,
                OnDelete: fk.OnDelete
            ));
        }
    }

    /// <summary>Plans foreign key additions and drops for an existing table.</summary>
    /// <param name="ops">Operation list to append to.</param>
    /// <param name="schema">Table schema.</param>
    /// <param name="doc">The table's document.</param>
    /// <param name="existing">Current table state.</param>
    /// <param name="allDocs">All desired documents (to resolve principals).</param>
    protected void PlanForeignKeyDiff(
    List<MigrationOperation> ops,
    string schema,
    VaultDocument doc,
    TableModel existing,
    IReadOnlyList<VaultDocument> allDocs)
    {
        var desired = new List<(string Column,
                                string PrincipalSchema,
                                string PrincipalTable,
                                string PrincipalColumn,
                                string ConstraintName,
                                string OnDelete)>();

        foreach (var fk in doc.ForeignKeys)
        {
            var (principalSchema, principalTable, principalColumn) =
                ResolveForeignKeyTarget(doc, fk, allDocs);

            // Unified, <= 60 chars, deterministic + unique
            var constraintName = ConstructConstraintName(
                "fk",
                doc.Name,
                fk.ColumnName,
                principalTable,
                principalColumn);

            desired.Add((fk.ColumnName, principalSchema, principalTable, principalColumn, constraintName, fk.OnDelete));
        }

        var existingFks = existing.ForeignKeys ?? Array.Empty<ForeignKeyModel>();

        // add missing
        foreach (var dfk in desired)
        {
            bool already = existingFks.Any(efk =>
                // Prefer constraint-name identity (most robust)
                string.Equals(efk.Name, dfk.ConstraintName, StringComparison.OrdinalIgnoreCase) ||

                // Or same FK triple
                (string.Equals(efk.Column, dfk.Column, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(efk.PrincipalTable, dfk.PrincipalTable, StringComparison.OrdinalIgnoreCase) &&
                 string.Equals(efk.PrincipalColumn, dfk.PrincipalColumn, StringComparison.OrdinalIgnoreCase)));

            if (!already)
            {
                ops.Add(new AddForeignKeyOperation(
                    Schema: schema,
                    Table: doc.Name,
                    ConstraintName: dfk.ConstraintName,
                    Column: dfk.Column,
                    PrincipalSchema: dfk.PrincipalSchema,
                    PrincipalTable: dfk.PrincipalTable,
                    PrincipalColumn: dfk.PrincipalColumn,
                    OnDelete: dfk.OnDelete));
            }
        }

        // drop extra
        foreach (var efk in existingFks)
        {
            bool stillDesired = desired.Any(dfk =>
                string.Equals(dfk.ConstraintName, efk.Name, StringComparison.OrdinalIgnoreCase) ||
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

    /// <summary>
    /// Resolve the principal schema, table, and column for a FK.
    /// Important: schema comes from the **principal vault's keyspace**, not the dependent.
    /// </summary>
    protected (string PrincipalSchema, string PrincipalTable, string PrincipalColumn) ResolveForeignKeyTarget(
        VaultDocument doc,
        VaultDocument.VaultForeignKeyDefinition fk,
        IReadOnlyList<VaultDocument> allDocs)
    {
        // We search across ALL documents (all keyspaces) so cross-schema FKs work.
        var principalDoc = allDocs.FirstOrDefault(d => d.Type == fk.PrincipalType)
            ?? throw new InvalidOperationException(
                $"Referenced vault type '{fk.PrincipalType.Name}' for '{doc.Type.Name}.{fk.PropertyName}' " +
                "not found among desired documents.");

        // principal schema MUST come from principal vault, not the dependent.
        var principalSchema = GetSchemaForDocument(principalDoc);

        // Map principal property name -> physical column.
        if (!principalDoc.Columns.TryGetValue(fk.PrincipalPropertyName, out var principalColumn))
        {
            // Fallback to camelCase if explicit column mapping not found
            principalColumn = VaultDocument.ToCamelCase(fk.PrincipalPropertyName);
        }

        // principalDoc.Name is the physical table name; principalSchema is its schema.
        return (principalSchema, principalDoc.Name, principalColumn);
    }

    /// <summary>When <c>StoreHistory</c> is set, plans <c>&lt;table&gt;_history</c>: all columns nullable plus a <c>timestamp</c> column, primary key = table key + timestamp, and an index per key column.</summary>
    /// <param name="ops">Operation list to append to.</param>
    /// <param name="schema">Target schema.</param>
    /// <param name="doc">The table's document.</param>
    /// <exception cref="InvalidOperationException">The model has no primary key or a field type is missing.</exception>
    protected void PlanHistoryTableForNew(
        List<MigrationOperation> ops,
        string schema,
        VaultDocument doc)
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

    /// <summary>Plans renames, type changes, added and dropped columns, unique constraint and index changes for an existing table.</summary>
    /// <param name="ops">Operation list to append to.</param>
    /// <param name="schema">Table schema.</param>
    /// <param name="doc">The table's document.</param>
    /// <param name="existing">Current table state.</param>
    /// <param name="allDocs">All desired documents.</param>
    protected void PlanExistingTableDiff(
        List<MigrationOperation> ops,
        string schema,
        VaultDocument doc,
        TableModel existing,
        IReadOnlyList<VaultDocument> allDocs)
    {
        var tableName = doc.Name;
        var schemaName = schema;

        var existingCols = new HashSet<string>(existing.Columns.Keys, StringComparer.OrdinalIgnoreCase);
        var desiredCols = new HashSet<string>(doc.Columns.Values, StringComparer.OrdinalIgnoreCase);

        // ── Rename detection via [VaultRenamedFrom] ──
        // Process renames first so they don't appear as drop+add
        var renamedOld = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var renamedNew = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (doc.RenamedColumns.Count > 0)
        {
            foreach (var (newCol, oldNames) in doc.RenamedColumns)
            {
                // Stacked [VaultRenamedFrom] — pick the first old name that exists in DB.
                // Allows preserving rename history: oldest→newest, planner uses first match.
                var matchedOld = oldNames.FirstOrDefault(old =>
                    existingCols.Contains(old) && !existingCols.Contains(newCol));

                if (matchedOld != null)
                {
                    ops.Add(new RenameColumnOperation(schemaName, tableName, matchedOld, newCol));
                    renamedOld.Add(matchedOld);
                    renamedNew.Add(newCol);
                }
            }
        }

        // ── Type change detection for columns that exist in both ──
        foreach (var col in desiredCols.Intersect(existingCols, StringComparer.OrdinalIgnoreCase))
        {
            if (renamedNew.Contains(col)) continue; // just renamed, type checked below

            var logical = doc.Columns.First(kv =>
                    string.Equals(kv.Value, col, StringComparison.OrdinalIgnoreCase))
                .Key;

            if (!doc.FieldTypes.TryGetValue(logical, out var clrType)) continue;
            if (!existing.Columns.TryGetValue(col, out var existingCol)) continue;

            var desiredStoreType = MapClrTypeToStoreType(clrType);
            if (!StoreTypesMatch(existingCol.StoreType, desiredStoreType))
            {
                ops.Add(new AlterColumnTypeOperation(schemaName, tableName, col,
                    existingCol.StoreType, desiredStoreType));
            }
        }

        // Also check type changes for renamed columns (rename + type change)
        foreach (var (newCol, oldNames) in doc.RenamedColumns)
        {
            var oldCol = oldNames.FirstOrDefault(renamedOld.Contains);
            if (oldCol == null) continue;
            if (!existing.Columns.TryGetValue(oldCol, out var existingCol)) continue;

            var logical = doc.Columns.First(kv =>
                    string.Equals(kv.Value, newCol, StringComparison.OrdinalIgnoreCase))
                .Key;

            if (!doc.FieldTypes.TryGetValue(logical, out var clrType)) continue;

            var desiredStoreType = MapClrTypeToStoreType(clrType);
            if (!StoreTypesMatch(existingCol.StoreType, desiredStoreType))
            {
                ops.Add(new AlterColumnTypeOperation(schemaName, tableName, newCol,
                    existingCol.StoreType, desiredStoreType));
            }
        }

        // Single-column unique keys created inline by ADD COLUMN ... UNIQUE (below); the unique
        // constraint diff must not add a second, identical constraint for them.
        var inlineUniqueCols = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // columns to add (exclude renamed columns)
        foreach (var col in desiredCols.Except(existingCols, StringComparer.OrdinalIgnoreCase)
                     .Where(c => !renamedNew.Contains(c)))
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
            var defaultSql = ResolveColumnDefaultSql(doc, col, clrType);

            var pkSet = new HashSet<string>(existing.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);
            var singleUniqueCols = new HashSet<string>(
                doc.UniqueKeys
                   .Where(uk => uk.Columns.Count == 1)
                   .Select(uk => uk.Columns[0]),
                StringComparer.OrdinalIgnoreCase);

            bool isPk = pkSet.Contains(col);
            bool isSingleUnique = singleUniqueCols.Contains(col);

            // use Document.NullableColumns for new columns as well
            bool isNullable = !isPk && doc.NullableColumns.Contains(col);

            var def = new ColumnDefinition(
                Name: col,
                StoreType: storeType,
                IsNullable: isNullable,
                IsUnique: isSingleUnique,
                DefaultSql: defaultSql);

            ops.Add(new AddColumnOperation(schemaName, tableName, def));
            if (isSingleUnique)
                inlineUniqueCols.Add(col);
        }

        // Unmapped columns (exclude renamed columns — handled above — and columns the 3rd pass drops
        // or copies from). Kept by default; dropped only with DropUnmappedColumns.
        var copySources = new HashSet<string>(doc.CopyFromColumns.Values, StringComparer.OrdinalIgnoreCase);
        var unmapped = existingCols.Except(desiredCols, StringComparer.OrdinalIgnoreCase)
            .Where(c => !renamedOld.Contains(c) && !doc.DeletedColumns.ContainsKey(c))
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        PlanUnmappedColumns(ops, schemaName, tableName, doc, existing, unmapped, copySources);

        // ---------- UNIQUE constraints diff (single + composite) ----------

        // desired unique constraints from Document
        var desiredUniqueByKey = new Dictionary<string, VaultDocument.UniqueKeyDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var uk in doc.UniqueKeys)
        {
            var key = NormalizeColumnSet(uk.Columns);
            if (!desiredUniqueByKey.ContainsKey(key))
            {
                desiredUniqueByKey[key] = uk;
            }
        }

        // existing unique constraints from DB
        var existingUniqueByKey = new Dictionary<string, UniqueConstraintModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var uc in existing.UniqueConstraints.Values)
        {
            var key = NormalizeColumnSet(uc.Columns);
            if (!existingUniqueByKey.ContainsKey(key))
            {
                existingUniqueByKey[key] = uc;
            }
        }

        // add missing uniques
        foreach (var (normalized, uk) in desiredUniqueByKey)
        {
            if (existingUniqueByKey.ContainsKey(normalized))
                continue;
            if (uk.Columns.Count == 1 && inlineUniqueCols.Contains(uk.Columns[0]))
                continue;

            var constraintName = BuildUniqueConstraintName(doc.Name, uk.Columns);
            ops.Add(new AddUniqueConstraintOperation(
                schemaName,
                tableName,
                constraintName,
                uk.Columns.ToArray()));
        }

        // drop uniques no longer desired
        foreach (var (normalized, existingUc) in existingUniqueByKey)
        {
            if (!desiredUniqueByKey.ContainsKey(normalized))
            {
                ops.Add(new DropConstraintOperation(schemaName, tableName, existingUc.Name));
            }
        }

        // ---------- indexes ----------

        var pkSet2 = new HashSet<string>(existing.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);

        var desiredIndexCols = new HashSet<string>(
            doc.Indexes ?? new List<string>(),
            StringComparer.OrdinalIgnoreCase);

        var sortCol = ResolveSortingColumn(doc);
        if (sortCol is not null)
            desiredIndexCols.Add(sortCol);

        var singleUniqueColumns = new HashSet<string>(
            doc.UniqueKeys
               .Where(uk => uk.Columns.Count == 1)
               .Select(uk => uk.Columns[0]),
            StringComparer.OrdinalIgnoreCase);

        desiredIndexCols.RemoveWhere(c =>
            pkSet2.Contains(c) ||
            singleUniqueColumns.Contains(c));

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

    /// <summary>When <c>StoreHistory</c> is set, creates the history table if missing or adds/drops its columns and key indexes to match the model.</summary>
    /// <param name="ops">Operation list to append to.</param>
    /// <param name="schema">Table schema.</param>
    /// <param name="doc">The table's document.</param>
    /// <param name="current">Current schema state.</param>
    protected void PlanHistoryTableDiff(
        List<MigrationOperation> ops,
        string schema,
        VaultDocument doc,
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

        // Unmapped history columns follow the table's policy: explicitly deleted columns are dropped,
        // the rest only with DropUnmappedColumns (history columns are created nullable, so keeping
        // them never breaks inserts).
        var unmappedHistory = existingCols.Except(desiredCols, StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var keptHistory = new List<string>();
        foreach (var col in unmappedHistory)
        {
            if (DropUnmappedColumns || doc.DeletedColumns.ContainsKey(col))
                ops.Add(new DropColumnOperation(schema, historyTable, col));
            else
                keptHistory.Add(col);
        }
        if (keptHistory.Count > 0)
        {
            var hist = existingHist.Columns;
            foreach (var col in keptHistory)
            {
                if (hist.TryGetValue(col, out var c) && !c.IsNullable && !c.HasDefault &&
                    !existingHist.PrimaryKeyColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
                    ops.Add(new RelaxNotNullOperation(schema, historyTable, col));
            }
            Logger.LogWarning(
                "Migration: history table {Schema}.{Table} has column(s) no longer mapped by {Model}: {Columns}. " +
                "They are kept (data preserved). Drop them with [VaultDropColumn(\"<name>\")] on the model or " +
                "set {ConfigKey}: true.",
                schema, historyTable, doc.Type.Name, string.Join(", ", keptHistory), DropUnmappedColumnsConfigKey);
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

    /// <summary>
    /// Plans what happens to columns of an existing table that the model no longer maps. With
    /// <see cref="DropUnmappedColumns"/> each is dropped (copy sources are left for the 3rd pass so their copy runs
    /// first). Otherwise each is kept and logged in one warning, and a <c>NOT NULL</c> column without a default that
    /// is not part of the primary key gets a <see cref="RelaxNotNullOperation"/> so inserts that omit it keep working.
    /// </summary>
    /// <param name="ops">Operation list to append to.</param>
    /// <param name="schema">Table schema.</param>
    /// <param name="table">Table name.</param>
    /// <param name="doc">The table's document.</param>
    /// <param name="existing">Current table state.</param>
    /// <param name="unmapped">Unmapped columns (already excluding renamed-from and explicitly deleted columns).</param>
    /// <param name="copySources">Columns some <see cref="Altruist.UORM.VaultColumnCopyAttribute"/> copies from.</param>
    protected virtual void PlanUnmappedColumns(
        List<MigrationOperation> ops,
        string schema,
        string table,
        VaultDocument doc,
        TableModel existing,
        IReadOnlyList<string> unmapped,
        IReadOnlySet<string> copySources)
    {
        if (unmapped.Count == 0)
            return;

        if (DropUnmappedColumns)
        {
            foreach (var col in unmapped)
            {
                if (copySources.Contains(col))
                    continue; // dropped in the 3rd pass, after the copy
                ops.Add(new DropColumnOperation(schema, table, col));
            }
            return;
        }

        var relaxed = new List<string>();
        foreach (var col in unmapped)
        {
            if (!existing.Columns.TryGetValue(col, out var c))
                continue;
            if (c.IsNullable || c.HasDefault)
                continue;
            if (existing.PrimaryKeyColumns.Contains(col, StringComparer.OrdinalIgnoreCase))
                continue; // cannot be relaxed; the model's key no longer matches the table (warned below)
            ops.Add(new RelaxNotNullOperation(schema, table, col));
            relaxed.Add(col);
        }

        Logger.LogWarning(
            "Migration: table {Schema}.{Table} has column(s) no longer mapped by {Model}: {Columns}. They are kept " +
            "(data preserved){Relaxed}. To drop them add [VaultDropColumn(\"<name>\")] to the model (or set " +
            "{ConfigKey}: true); to keep the data under a new property use [VaultRenamedFrom(\"<old name>\")].",
            schema, table, doc.Type.Name, string.Join(", ", unmapped),
            relaxed.Count == 0 ? "" : "; NOT NULL relaxed to allow NULL on: " + string.Join(", ", relaxed),
            DropUnmappedColumnsConfigKey);
    }

    // ---------- helpers ----------

    /// <summary>Physical primary key columns from the model's primary key attribute (unmapped names fall back to camelCase).</summary>
    /// <param name="doc">The document.</param>
    /// <returns>Primary key columns; empty when none declared.</returns>
    protected static List<string> ResolvePrimaryKeyColumns(VaultDocument doc)
    {
        var result = new List<string>();
        var keys = doc.PrimaryKey?.Keys ?? Array.Empty<string>();
        foreach (var keyProp in keys)
        {
            if (doc.Columns.TryGetValue(keyProp, out var col))
                result.Add(col);
            else
                result.Add(VaultDocument.ToCamelCase(keyProp));
        }
        return result;
    }

    /// <summary>Physical column of <see cref="Altruist.UORM.VaultSortingByAttribute"/>, or null; it is indexed like a <see cref="Altruist.UORM.VaultColumnIndexAttribute"/> column.</summary>
    /// <param name="doc">The document.</param>
    /// <returns>The column or null.</returns>
    protected static string? ResolveSortingColumn(VaultDocument doc)
    {
        var sortProp = doc.SortingBy?.Name;
        if (string.IsNullOrWhiteSpace(sortProp))
            return null;

        return doc.Columns.TryGetValue(sortProp, out var col)
            ? col
            : VaultDocument.ToCamelCase(sortProp);
    }

    /// <summary>Builds <c>{prefix}_{parts}</c> (lower-cased); names longer than <see cref="MaxConstraintNameLength"/> are truncated with a 12-hex-char SHA-256 suffix. Mirrors <see cref="ConstraintUtil.ConstructConstraintName"/>.</summary>
    /// <param name="prefix">Name prefix, e.g. <c>uq</c>.</param>
    /// <param name="parts">Name parts; blanks are skipped.</param>
    /// <returns>Deterministic constraint name.</returns>
    /// <exception cref="ArgumentException"><paramref name="prefix"/> is blank.</exception>
    protected static string ConstructConstraintName(string prefix, params string[] parts)
    {
        if (string.IsNullOrWhiteSpace(prefix))
            throw new ArgumentException("Constraint prefix must be provided.", nameof(prefix));

        // Keep deterministic naming. We normalize casing only (don’t over-sanitize;
        // executor quotes identifiers anyway).
        static string NormalizePart(string s)
            => string.IsNullOrWhiteSpace(s) ? "" : s.Trim().ToLowerInvariant();

        var normalizedParts = parts
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizePart)
            .ToArray();

        var full = $"{prefix}_{string.Join("_", normalizedParts)}";

        if (full.Length <= MaxConstraintNameLength)
            return full;

        // Deterministic short hash of the *full* name (so uniqueness is preserved).
        // 12 hex chars = 48 bits; extremely low collision risk for schema objects.
        var hash = ShortHexHash(full, hexChars: 12);

        // Reserve "_{hash}" suffix
        var reserve = 1 + hash.Length;
        var keepLen = MaxConstraintNameLength - reserve;

        // Keep a stable prefix portion, trim trailing '_' so formatting stays nice
        var kept = full[..keepLen].TrimEnd('_');

        // Ensure we don’t end up with empty prefix part after trimming
        if (string.IsNullOrWhiteSpace(kept))
            kept = prefix.ToLowerInvariant();

        return $"{kept}_{hash}";
    }

    private static string ShortHexHash(string input, int hexChars)
    {
        if (hexChars <= 0)
            throw new ArgumentOutOfRangeException(nameof(hexChars));

        // SHA256 -> hex; take prefix
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = sha.ComputeHash(bytes);

        // Convert.ToHexString gives uppercase; normalize to lowercase
        var hex = Convert.ToHexString(hash).ToLowerInvariant();

        return hexChars >= hex.Length ? hex : hex[..hexChars];
    }
}
