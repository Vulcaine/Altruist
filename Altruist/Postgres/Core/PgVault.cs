// PgVault.cs
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
        IReadOnlyList<string> primaryKeyColumns)
    {
        if (primaryKeyColumns.Count == 0)
            throw new ArgumentException("At least one primary key column must be specified.", nameof(primaryKeyColumns));

        var alias = "t";
        var versionCol = VersionColumn();
        var storageIdCol = StorageIdColumn();

        var colSql = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var valsSql = string.Join(", ", columns.Select(_ => "?"));

        var pkSql = string.Join(", ", primaryKeyColumns.Select(pk => $"\"{pk}\""));
        var setSql = BuildSetClauses(columns, primaryKeyColumns, versionCol, alias);

        return
            $"INSERT INTO {qualifiedTable} AS {alias} ({colSql}) VALUES ({valsSql}) " +
            $"ON CONFLICT ({pkSql}) DO UPDATE SET {setSql} " +
            $"WHERE {alias}.\"{versionCol}\" = EXCLUDED.\"{versionCol}\" " +
            $"RETURNING {alias}.\"{storageIdCol}\" AS \"{StorageIdLogical}\", {alias}.\"{versionCol}\" AS \"{VersionLogical}\"";
    }

    protected override string BuildBatchUpsertSql_VersionedReturning(
     string qualifiedTable,
     IReadOnlyList<string> columns,
     IReadOnlyList<string> primaryKeyColumns,
     int rowCount)
    {
        if (rowCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(rowCount));
        if (primaryKeyColumns.Count == 0)
            throw new ArgumentException("At least one primary key column must be specified.", nameof(primaryKeyColumns));

        var alias = "t";
        var inputAlias = "v";

        var versionCol = VersionColumn();
        var storageIdCol = StorageIdColumn();

        // (?,?,...) repeated rowCount times
        var rowPlaceholders = "(" + string.Join(", ", columns.Select(_ => "?")) + ")";
        var valuesSql = string.Join(", ", Enumerable.Repeat(rowPlaceholders, rowCount));

        // column list used for VALUES alias AND for INSERT/SELECT
        var colListSql = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var pkListSql = string.Join(", ", primaryKeyColumns.Select(pk => $"\"{pk}\""));

        // join condition t.pk = v.pk (supports composite keys)
        var pkJoin = string.Join(" AND ",
            primaryKeyColumns.Select(pk => $"{alias}.\"{pk}\" = {inputAlias}.\"{pk}\""));

        // SET clauses for non-PK, non-version columns, plus version bump
        var pkSet = new HashSet<string>(primaryKeyColumns, StringComparer.OrdinalIgnoreCase);

        var setClauses = new List<string>();
        foreach (var c in columns)
        {
            if (pkSet.Contains(c))
                continue;

            if (string.Equals(c, versionCol, StringComparison.OrdinalIgnoreCase))
                continue;

            setClauses.Add($"\"{c}\" = EXCLUDED.\"{c}\"");
        }

        setClauses.Add($"\"{versionCol}\" = {alias}.\"{versionCol}\" + 1");
        var setSql = string.Join(", ", setClauses);

        // Atomic strategy:
        // - lock existing rows
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
    JOIN input AS {inputAlias} ON {pkJoin}
    FOR UPDATE
),
mismatch AS (
    SELECT 1
    FROM {qualifiedTable} AS {alias}
    JOIN input AS {inputAlias} ON {pkJoin}
    WHERE {alias}.""{versionCol}"" <> {inputAlias}.""{versionCol}""
    LIMIT 1
),
upserted AS (
    INSERT INTO {qualifiedTable} AS {alias} ({colListSql})
    SELECT {colListSql}
    FROM input
    WHERE NOT EXISTS (SELECT 1 FROM mismatch)
    ON CONFLICT ({pkListSql}) DO UPDATE
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
        IReadOnlyList<string> primaryKeyColumns,
        string versionCol,
        string tableAlias)
    {
        var pkSet = new HashSet<string>(primaryKeyColumns, StringComparer.OrdinalIgnoreCase);

        // update all non-PK, non-version columns from EXCLUDED, then bump version
        var sets = new List<string>(columns.Count);
        foreach (var c in columns)
        {
            if (pkSet.Contains(c))
                continue;

            if (string.Equals(c, versionCol, StringComparison.OrdinalIgnoreCase))
                continue;

            sets.Add($"\"{c}\" = EXCLUDED.\"{c}\"");
        }

        sets.Add($"\"{versionCol}\" = {tableAlias}.\"{versionCol}\" + 1");
        return string.Join(", ", sets);
    }
}
