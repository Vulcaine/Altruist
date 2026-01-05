/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Linq.Expressions;

namespace Altruist.Persistence.Postgres;

/// <summary>
/// PostgreSQL vault with a fluent API similar to the CQL vault.
/// SQL-generic behavior lives in SqlVault; PgVault provides translation + Postgres dialect SQL.
/// </summary>
public class PgVault<TVaultModel> : SqlVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    public PgVault(
        ISqlDatabaseProvider databaseProvider,
        IKeyspace schema,
        VaultDocument document)
        : this(databaseProvider, schema, document, new QueryState())
    {
    }

    protected PgVault(
        ISqlDatabaseProvider databaseProvider,
        IKeyspace schema,
        VaultDocument document,
        QueryState state)
        : base(databaseProvider, schema, document, state)
    {
    }

    protected override SqlVault<TVaultModel> Create(QueryState state)
        => new PgVault<TVaultModel>(_databaseProvider, Keyspace, VaultDocument, state);

    protected override IHistoricalVault<TVaultModel> CreateHistoryVault()
        => new PgHistoricalVault<TVaultModel>(this);

    // ------------------------ Translation ------------------------

    protected override string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate)
        => PgQueryTranslator.Where(predicate, VaultDocument);

    protected override string ConvertOrderByToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
        => PgQueryTranslator.OrderBy(keySelector, VaultDocument);

    protected override string ConvertOrderByDescendingToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
        => PgQueryTranslator.OrderBy(keySelector, VaultDocument);

    protected override IEnumerable<string> TranslateSelect<TResult>(Expression<Func<TVaultModel, TResult>> selector)
        => PgQueryTranslator.Select(selector, VaultDocument);

    protected override string QuoteIdent(string ident)
        => $"\"{ident.Replace("\"", "\"\"")}\"";

    // ------------------------ Upsert dialect (Versioned) ------------------------

    protected override string BuildUpsertSql_VersionedReturning(
        string qualifiedTable,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> conflictKeyColumns,
        string? conflictConstraintName)
    {
        if (conflictKeyColumns.Count == 0)
            throw new ArgumentException("At least one conflict key column must be specified.", nameof(conflictKeyColumns));

        var alias = "t";
        var versionCol = VersionColumn();
        var storageIdCol = StorageIdColumn();

        var colSql = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var valsSql = string.Join(", ", columns.Select(_ => "?"));

        // Conflict target:
        // - If we have a unique constraint name, use ON CONSTRAINT
        // - Else fallback to column list (PK mode)
        string conflictSql = !string.IsNullOrWhiteSpace(conflictConstraintName)
            ? $"ON CONFLICT ON CONSTRAINT {QuoteIdent(conflictConstraintName)}"
            : $"ON CONFLICT ({string.Join(", ", conflictKeyColumns.Select(pk => $"\"{pk}\""))})";

        var setSql = BuildSetClauses(columns, conflictKeyColumns, versionCol, storageIdCol, alias);

        return
            $"INSERT INTO {qualifiedTable} AS {alias} ({colSql}) VALUES ({valsSql}) " +
            $"{conflictSql} DO UPDATE SET {setSql} " +
            $"WHERE {alias}.\"{versionCol}\" = EXCLUDED.\"{versionCol}\" " +
            $"RETURNING {alias}.\"{storageIdCol}\" AS \"{StorageIdLogical}\", {alias}.\"{versionCol}\" AS \"{VersionLogical}\"";
    }

    protected override string BuildBatchUpsertSql_VersionedReturning(
        string qualifiedTable,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> conflictKeyColumns,
        string? conflictConstraintName,
        int rowCount)
    {
        if (rowCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowCount));
        if (conflictKeyColumns.Count == 0)
            throw new ArgumentException("At least one conflict key column must be specified.", nameof(conflictKeyColumns));

        var alias = "t";
        var inputAlias = "v";

        var versionCol = VersionColumn();
        var storageIdCol = StorageIdColumn();

        // (?,?,...) repeated rowCount times
        var rowPlaceholders = "(" + string.Join(", ", columns.Select(_ => "?")) + ")";
        var valuesSql = string.Join(", ", Enumerable.Repeat(rowPlaceholders, rowCount));

        // column list used for VALUES alias AND for INSERT/SELECT
        var colListSql = string.Join(", ", columns.Select(c => $"\"{c}\""));

        // join condition t.key = v.key (supports composite keys)
        var keyJoin = string.Join(" AND ",
            conflictKeyColumns.Select(k => $"{alias}.\"{k}\" = {inputAlias}.\"{k}\""));

        // Conflict target SQL
        string conflictSql = !string.IsNullOrWhiteSpace(conflictConstraintName)
            ? $"ON CONFLICT ON CONSTRAINT {QuoteIdent(conflictConstraintName)}"
            : $"ON CONFLICT ({string.Join(", ", conflictKeyColumns.Select(k => $"\"{k}\""))})";

        var setSql = BuildSetClauses(columns, conflictKeyColumns, versionCol, storageIdCol, alias);

        // Atomic strategy:
        // - lock existing rows matched by the conflict key
        // - if ANY mismatch -> mismatch has row -> INSERT is skipped -> upserted returns 0 rows
        // - caller detects count != rowCount and throws OptimisticConcurrencyException
        return
$@"
WITH input AS (
    SELECT * FROM (VALUES {valuesSql}) AS {inputAlias}({colListSql})
),
locked AS (
    SELECT {alias}.""{versionCol}""
    FROM {qualifiedTable} AS {alias}
    JOIN input AS {inputAlias} ON {keyJoin}
    FOR UPDATE
),
mismatch AS (
    SELECT 1
    FROM {qualifiedTable} AS {alias}
    JOIN input AS {inputAlias} ON {keyJoin}
    WHERE {alias}.""{versionCol}"" <> {inputAlias}.""{versionCol}""
    LIMIT 1
),
upserted AS (
    INSERT INTO {qualifiedTable} AS {alias} ({colListSql})
    SELECT {colListSql}
    FROM input
    WHERE NOT EXISTS (SELECT 1 FROM mismatch)
    {conflictSql} DO UPDATE
        SET {setSql}
        WHERE {alias}.""{versionCol}"" = EXCLUDED.""{versionCol}""
    RETURNING {alias}.""{storageIdCol}"" AS ""{StorageIdLogical}"",
              {alias}.""{versionCol}"" AS ""{VersionLogical}""
)
SELECT ""{StorageIdLogical}"", ""{VersionLogical}"" FROM upserted
;";
    }

    private static string BuildSetClauses(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> conflictKeyColumns,
        string versionCol,
        string storageIdCol,
        string tableAlias)
    {
        var conflictSet = new HashSet<string>(conflictKeyColumns, StringComparer.OrdinalIgnoreCase);

        var sets = new List<string>(columns.Count);

        foreach (var c in columns)
        {
            // never update conflict key columns
            if (conflictSet.Contains(c))
                continue;

            // never update version column directly (we bump)
            if (string.Equals(c, versionCol, StringComparison.OrdinalIgnoreCase))
                continue;

            // never update storage id column (PK) even if conflict key is different
            if (string.Equals(c, storageIdCol, StringComparison.OrdinalIgnoreCase))
                continue;

            sets.Add($"\"{c}\" = EXCLUDED.\"{c}\"");
        }

        // bump version
        sets.Add($"\"{versionCol}\" = {tableAlias}.\"{versionCol}\" + 1");

        return string.Join(", ", sets);
    }
}
