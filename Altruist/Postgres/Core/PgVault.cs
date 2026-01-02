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
        var versionCol = VersionColumn();
        var storageIdCol = StorageIdColumn();

        var colListSql = string.Join(", ", columns.Select(c => $"\"{c}\""));
        var pkSql = string.Join(", ", primaryKeyColumns.Select(pk => $"\"{pk}\""));

        var rowPlaceholders = "(" + string.Join(", ", columns.Select(_ => "?")) + ")";
        var valuesSql = string.Join(", ", Enumerable.Repeat(rowPlaceholders, rowCount));

        var setSql = BuildSetClauses(columns, primaryKeyColumns, versionCol, alias);

        // Atomicity: if any row hits version mismatch, it won’t be updated -> upserted count < input count.
        // We force statement failure using (1/0) so caller gets one exception and no partial writes.
        return
$@"
WITH input AS (
    SELECT * FROM (VALUES {valuesSql}) AS v({colListSql})
),
upserted AS (
    INSERT INTO {qualifiedTable} AS {alias} ({colListSql})
    SELECT {colListSql} FROM input
    ON CONFLICT ({pkSql}) DO UPDATE
        SET {setSql}
        WHERE {alias}.""{versionCol}"" = EXCLUDED.""{versionCol}""
    RETURNING {alias}.""{storageIdCol}"" AS ""{StorageIdLogical}"",
              {alias}.""{versionCol}"" AS ""{VersionLogical}""
),
chk AS (
    SELECT (SELECT COUNT(*) FROM input) AS expected,
           (SELECT COUNT(*) FROM upserted) AS actual
)
SELECT u.""{StorageIdLogical}"" AS ""{StorageIdLogical}"",
       u.""{VersionLogical}"" AS ""{VersionLogical}""
FROM upserted u
CROSS JOIN chk
WHERE (CASE WHEN chk.expected = chk.actual THEN 1 ELSE (1/0) END) = 1
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
