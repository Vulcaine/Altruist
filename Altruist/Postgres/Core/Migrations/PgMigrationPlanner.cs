/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
*/

using System.Reflection;

using Altruist.Persistence;

using static Altruist.Persistence.Document;

namespace Altruist.Migrations.Postgres;

[Service(typeof(IMigrationPlanner))]
[ConditionalOnConfig("altruist:persistence:database:provider", havingValue: "postgres")]
public sealed class PostgresMigrationPlanner : IMigrationPlanner
{
    public IReadOnlyList<MigrationOperation> Plan(
    DatabaseModel current,
    IReadOnlyList<Document> desiredDocuments,
    string schemaName)
    {
        var ops = new List<MigrationOperation>();
        var schemaLower = (schemaName ?? "public").Trim().ToLowerInvariant();

        // ─────────────────────────────────────────────
        // 1st pass: tables, columns, uniques, indexes, history
        // ─────────────────────────────────────────────
        foreach (var doc in desiredDocuments)
        {
            var tableName = doc.Name;
            current.TryGetTable(tableName, out var existingTable);

            if (existingTable is null)
            {
                PlanNewTable(ops, schemaLower, doc, desiredDocuments);
                PlanHistoryTableForNew(ops, schemaLower, doc);
            }
            else
            {
                PlanExistingTableDiff(ops, schemaLower, doc, existingTable, desiredDocuments);
                PlanHistoryTableDiff(ops, schemaLower, doc, current);
            }
        }

        // ─────────────────────────────────────────────
        // 2nd pass: foreign keys (after all tables exist)
        // ─────────────────────────────────────────────
        foreach (var doc in desiredDocuments)
        {
            var tableName = doc.Name;
            current.TryGetTable(tableName, out var existingTable);

            if (existingTable is null)
            {
                PlanForeignKeysForNewTable(ops, schemaLower, doc, desiredDocuments);
            }
            else
            {
                PlanForeignKeyDiff(ops, schemaLower, doc, existingTable, desiredDocuments);
            }
        }

        return ops;
    }

    private static void PlanNewTable(
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
            var logicalName = kv.Key;
            var columnName = kv.Value;

            var prop = doc.Type.GetProperty(logicalName, BindingFlags.Public | BindingFlags.Instance)
                       ?? throw new InvalidOperationException(
                           $"Property '{logicalName}' not found on '{doc.Type.Name}'.");

            var storeType = MapTypeToSql(prop.PropertyType);

            bool isPk = pkSet.Contains(columnName);
            bool isUnique = uniqueSet.Contains(columnName) || isPk;
            bool isNullable = !isPk;

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

        // indexes (non-unique + non-pk)
        var indexColumns = new HashSet<string>(doc.Indexes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
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

    private static void PlanForeignKeysForNewTable(
    List<MigrationOperation> ops,
    string schema,
    Document doc,
    IReadOnlyList<Document> allDocs)
    {
        foreach (var fk in doc.ForeignKeys)
        {
            var (principalTable, principalColumn) =
                ResolveForeignKeyTarget(doc, fk, allDocs);

            var constraintName = $"fk_{doc.Name}_{fk.ColumnName}_{principalTable}_{principalColumn}";

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

    private static void PlanForeignKeyDiff(
    List<MigrationOperation> ops,
    string schema,
    Document doc,
    TableModel existing,
    IReadOnlyList<Document> allDocs)
    {
        // desired FKs from Document
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

    private static (string PrincipalTable, string PrincipalColumn) ResolveForeignKeyTarget(
    Document doc,
    VaultForeignKeyDefinition fk,
    IReadOnlyList<Document> allDocs)
    {
        var principalDoc = allDocs.FirstOrDefault(d => d.Type == fk.PrincipalType)
            ?? throw new InvalidOperationException(
                $"Referenced vault type '{fk.PrincipalType.Name}' for '{doc.Type.Name}.{fk.PropertyName}' not found among desired documents.");

        if (!principalDoc.Columns.TryGetValue(fk.PrincipalPropertyName, out var principalColumn))
        {
            principalColumn = Document.ToCamelCase(fk.PrincipalPropertyName);
        }

        return (principalDoc.Name, principalColumn);
    }

    private static void PlanHistoryTableForNew(List<MigrationOperation> ops, string schema, Document doc)
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
            var prop = doc.Type.GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance)
                       ?? throw new InvalidOperationException(
                           $"Property '{kv.Key}' not found on '{doc.Type.Name}' for history.");

            var storeType = MapTypeToSql(prop.PropertyType);

            histCols.Add(new ColumnDefinition(
                Name: kv.Value,
                StoreType: storeType,
                IsNullable: true,    // history columns usually nullable
                IsUnique: false));
        }

        histCols.Add(new ColumnDefinition(
            Name: "timestamp",
            StoreType: "timestamptz",
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

    private static void PlanExistingTableDiff(
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

            var prop = doc.Type.GetProperty(logical, BindingFlags.Public | BindingFlags.Instance)
                       ?? throw new InvalidOperationException(
                           $"Property '{logical}' not found on '{doc.Type.Name}' for column '{col}'.");

            var storeType = MapTypeToSql(prop.PropertyType);

            var pkSet = new HashSet<string>(existing.PrimaryKeyColumns, StringComparer.OrdinalIgnoreCase);
            var uniqueSet = new HashSet<string>(doc.UniqueKeys, StringComparer.OrdinalIgnoreCase);

            bool isPk = pkSet.Contains(col);
            bool isUnique = uniqueSet.Contains(col) || isPk;
            bool isNullable = !isPk;

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
        var desiredIndexCols = new HashSet<string>(doc.Indexes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
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


    // ---------- HISTORY TABLE DIFF ----------

    private static void PlanHistoryTableDiff(
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

        var existingCols = new HashSet<string>(existingHist.Columns.Keys, StringComparer.OrdinalIgnoreCase);

        // add columns
        foreach (var col in desiredCols.Except(existingCols, StringComparer.OrdinalIgnoreCase))
        {
            string storeType;

            if (string.Equals(col, "timestamp", StringComparison.OrdinalIgnoreCase))
            {
                storeType = "timestamptz";
            }
            else
            {
                var logical = doc.Columns.First(kv =>
                        string.Equals(kv.Value, col, StringComparison.OrdinalIgnoreCase))
                    .Key;

                var prop = doc.Type.GetProperty(logical, BindingFlags.Public | BindingFlags.Instance)
                           ?? throw new InvalidOperationException(
                               $"Property '{logical}' not found on '{doc.Type.Name}' for history column '{col}'.");

                storeType = MapTypeToSql(prop.PropertyType);
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

    private static List<string> ResolvePrimaryKeyColumns(Document doc)
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

    private static string? ResolveSortingColumn(Document doc)
    {
        var sortProp = doc.SortingBy?.Name;
        if (string.IsNullOrWhiteSpace(sortProp))
            return null;

        return doc.Columns.TryGetValue(sortProp, out var col)
            ? col
            : Document.ToCamelCase(sortProp);
    }

    private static string MapTypeToSql(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
            type = Nullable.GetUnderlyingType(type)!;

        if (type == typeof(string))
            return "text";
        if (type == typeof(bool))
            return "boolean";
        if (type == typeof(byte))
            return "smallint";
        if (type == typeof(short))
            return "smallint";
        if (type == typeof(int))
            return "integer";
        if (type == typeof(long))
            return "bigint";
        if (type == typeof(float))
            return "real";
        if (type == typeof(double))
            return "double precision";
        if (type == typeof(decimal))
            return "numeric";
        if (type == typeof(DateTime))
            return "timestamp";
        if (type == typeof(DateTimeOffset))
            return "timestamptz";
        if (type == typeof(Guid))
            return "uuid";
        if (type == typeof(byte[]))
            return "bytea";
        if (type == typeof(TimeSpan))
            return "interval";

        if (type.IsArray)
        {
            var elem = type.GetElementType()!;

            if (elem == typeof(short))
                return "smallint[]";
            if (elem == typeof(int))
                return "integer[]";
            if (elem == typeof(long))
                return "bigint[]";
            if (elem == typeof(string))
                return "text[]";
            if (elem == typeof(float))
                return "real[]";
            if (elem == typeof(double))
                return "double precision[]";
            if (elem == typeof(Guid))
                return "uuid[]";

            return "jsonb";
        }

        if (typeof(System.Collections.IEnumerable).IsAssignableFrom(type) && type != typeof(string))
            return "jsonb";

        if (type.IsEnum)
            return "text";

        return "jsonb";
    }
}
