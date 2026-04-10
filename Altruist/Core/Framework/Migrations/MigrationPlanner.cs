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
    /// Unique constraints on this table, keyed by constraint name.
    /// Each constraint can span one or more columns.
    /// </summary>
    public IReadOnlyDictionary<string, UniqueConstraintModel> UniqueConstraints { get; }

    public IReadOnlyDictionary<string, IndexModel> Indexes { get; }

    // NEW:
    public IReadOnlyList<ForeignKeyModel> ForeignKeys { get; }

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

public sealed class UniqueConstraintModel
{
    public string Name { get; }

    /// <summary>
    /// Physical column names participating in this UNIQUE constraint (in DB order).
    /// </summary>
    public List<string> Columns { get; }

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

/// <summary>
/// Planner now takes:
/// - all current schemas (DatabaseModel per schema),
/// - all desired Documents (already ordered by dependency).
/// Each Document knows its own schema (via VaultAttribute.Keyspace).
/// </summary>
public interface IMigrationPlanner
{
    IReadOnlyList<MigrationOperation> Plan(
        IReadOnlyDictionary<string, DatabaseModel> currentBySchema,
        IReadOnlyList<VaultDocument> desiredDocuments);
}

public abstract class AbstractMigrationPlanner : IMigrationPlanner
{
    protected const int MaxConstraintNameLength = 60;

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
            if (doc.DeletedColumns.Count == 0) continue;
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

    protected static string NormalizeColumnSet(IEnumerable<string> columns)
    {
        return string.Join("|",
            columns
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Select(c => c.ToLowerInvariant())
                .OrderBy(c => c, StringComparer.Ordinal));
    }

    protected static string BuildUniqueConstraintName(string tableName, IReadOnlyList<string> columns)
    {
        // Deterministic, safe-length naming.
        // Example: uq_character_inventory_slot_kind
        return ConstructConstraintName("uq", tableName, string.Join("_", columns));
    }

    // ---------- core planning logic (provider-agnostic, uses hooks above) ----------

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

            bool isPk = pkSet.Contains(columnName);
            bool isSingleUnique = singleUniqueCols.Contains(columnName);
            bool isNullable = !isPk && doc.NullableColumns.Contains(columnName);

            columns.Add(new ColumnDefinition(
                Name: columnName,
                StoreType: storeType,
                IsNullable: isNullable,
                IsUnique: isSingleUnique)); // column-level UNIQUE only for single-col unique keys
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
            if (!string.Equals(existingCol.StoreType, desiredStoreType, StringComparison.OrdinalIgnoreCase))
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
            if (!string.Equals(existingCol.StoreType, desiredStoreType, StringComparison.OrdinalIgnoreCase))
            {
                ops.Add(new AlterColumnTypeOperation(schemaName, tableName, newCol,
                    existingCol.StoreType, desiredStoreType));
            }
        }

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
                IsUnique: isSingleUnique);

            ops.Add(new AddColumnOperation(schemaName, tableName, def));
        }

        // columns to drop (exclude renamed columns — they were handled above)
        foreach (var col in existingCols.Except(desiredCols, StringComparer.OrdinalIgnoreCase)
                     .Where(c => !renamedOld.Contains(c)))
        {
            ops.Add(new DropColumnOperation(schemaName, tableName, col));
        }

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

    protected static string? ResolveSortingColumn(VaultDocument doc)
    {
        var sortProp = doc.SortingBy?.Name;
        if (string.IsNullOrWhiteSpace(sortProp))
            return null;

        return doc.Columns.TryGetValue(sortProp, out var col)
            ? col
            : VaultDocument.ToCamelCase(sortProp);
    }

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
