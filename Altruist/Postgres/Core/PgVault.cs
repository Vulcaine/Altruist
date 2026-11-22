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
/// PostgreSQL implementation of <see cref="IVault{TVaultModel}"/>: a typed, immutable, fluent query and
/// save API over one <c>[Vault]</c> table. Provider-agnostic behaviour (query state, select building,
/// batching, history, version-based optimistic concurrency) lives in <see cref="SqlVault{TVaultModel}"/>;
/// this class adds the Postgres expression translation and the Postgres <c>INSERT ... ON CONFLICT</c> upsert dialect.
/// </summary>
/// <remarks>
/// <para><b>Which Postgres API to use</b></para>
/// <list type="bullet">
/// <item><description><b>Vault (this type, injected as <c>IVault&lt;TModel&gt;</c>)</b>: single-table CRUD on a
/// <c>[Vault]</c> model, simple filters, ordering and paging, optimistic-concurrency saves and optional history.
/// The default choice.</description></item>
/// <item><description><b>Prefabs (<see cref="IPrefabs"/> / <see cref="IPrefabQuery{TPrefab}"/>)</b>: an aggregate
/// root plus its component rows from other vault tables, loaded together (eager <c>Include</c>) and saved together
/// in one transaction.</description></item>
/// <item><description><b>Raw SQL (<see cref="ISqlDatabaseProvider"/>, e.g. <see cref="PgSqlDbProvider"/>)</b>:
/// anything the translators cannot express (joins without the querying layer, aggregates, <c>LIKE</c>, <c>IN</c>,
/// bulk <c>UPDATE</c>, DDL). Use <c>?</c> placeholders with a parameter list.</description></item>
/// </list>
/// <para><b>Supported LINQ shapes</b> (translated by an internal translator; values are evaluated client-side and
/// inlined as escaped SQL literals, not bound as parameters):</para>
/// <list type="bullet">
/// <item><description><c>Where</c>: comparisons (<c>==</c>, <c>!=</c>, <c>&lt;</c>, <c>&lt;=</c>, <c>&gt;</c>,
/// <c>&gt;=</c>) between a mapped property and a value (either side; captured variables and computed expressions are
/// evaluated once at translation time) or between two mapped properties of the row, combined with <c>&amp;&amp;</c> /
/// <c>||</c>. Null follows C# semantics: <c>x.Prop == null</c> is <c>IS NULL</c>, <c>!= null</c> is
/// <c>IS NOT NULL</c>, an ordering comparison with null is never true, and <c>x.Prop != value</c> also matches rows
/// whose column is NULL (<c>IS DISTINCT FROM</c>). <c>x.Prop.Value</c> on a nullable property reads the column.
/// Anything else (a bare bool property, <c>!</c>, method calls such as <c>Contains</c>/<c>StartsWith</c>,
/// arithmetic on the column, nested members such as <c>x.A.B</c>, unmapped properties) throws
/// <see cref="NotSupportedException"/>.</description></item>
/// <item><description><c>OrderBy</c>/<c>OrderByDescending</c>: a single mapped property <c>x =&gt; x.Prop</c>.
/// Multiple calls append further sort keys in call order.</description></item>
/// <item><description><c>Skip</c>/<c>Take</c> compose like LINQ into one <c>LIMIT</c>/<c>OFFSET</c> window; filters and
/// sort keys must be added before them. <c>CountAsync</c> and <c>DeleteAsync</c> act on that window.</description></item>
/// <item><description><c>SelectAsync</c>: <c>x =&gt; new T { A = x.A, B = x.C }</c>; each assigned member reads the
/// column of the property it is assigned from.</description></item>
/// </list>
/// <para>
/// Column names come from the model's <see cref="VaultDocument"/> (property → column map); a property that is not a
/// <c>[VaultColumn]</c> throws <see cref="NotSupportedException"/>.
/// </para>
/// <para>
/// Each fluent call returns a new vault instance; the injected singleton is never mutated, so it is safe to
/// share across threads. Every terminal call opens a pooled connection, or joins the ambient transaction
/// (see <see cref="TransactionalDecorator{T}"/> and <see cref="ISqlTransactionProvider"/>).
/// </para>
/// <para>
/// <b>Saving</b>: <c>SaveAsync</c>/<c>SaveBatchAsync</c> call <see cref="IVaultModel.OnSave"/> (assigns id,
/// timestamp, type), then upsert. The conflict target is the first <c>[VaultUniqueKey]</c> when one exists,
/// otherwise the primary key. An update only applies when the stored <c>version</c> equals the entity's
/// <c>Version</c>, and bumps it by one; a mismatch throws <see cref="OptimisticConcurrencyException"/>. The
/// batch form is atomic: one mismatching row aborts the whole batch.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// [Vault("score", StoreHistory: true)]
/// public class ScoreVault : VaultModel
/// {
///     public string OwnerId { get; set; } = "";
///     public int Points { get; set; }
/// }
///
/// public sealed class ScoreService(IVault&lt;ScoreVault&gt; scores)
/// {
///     public async Task&lt;List&lt;ScoreVault&gt;&gt; TopAsync(int min, CancellationToken ct)
///         =&gt; await scores.Where(s =&gt; s.Points &gt;= min)
///                         .OrderByDescending(s =&gt; s.Points)
///                         .Take(10)
///                         .ToListAsync(ct);
///
///     public async Task AddAsync(string owner, int points, CancellationToken ct)
///     {
///         var row = new ScoreVault { OwnerId = owner, Points = points };
///         await scores.SaveAsync(row, saveHistory: true, ct: ct); // row.StorageId / Version are filled in
///     }
///
///     public Task&lt;List&lt;ScoreVault&gt;&gt; HistoryAsync(string owner, DateTime from, DateTime to)
///         =&gt; scores.History.Where(s =&gt; s.OwnerId == owner).ToListAsync(from, to);
/// }
/// </code>
/// </example>
public class PgVault<TVaultModel> : SqlVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    /// <summary>
    /// Creates a vault with an empty query. Normally created by <see cref="PostgresServiceFactory"/>;
    /// inject <c>IVault&lt;TModel&gt;</c> instead of constructing one.
    /// </summary>
    /// <param name="databaseProvider">Provider that executes the generated SQL.</param>
    /// <param name="schema">Postgres schema (keyspace) holding the table.</param>
    /// <param name="document">Table metadata (name, columns, keys) for <typeparamref name="TVaultModel"/>.</param>
    public PgVault(
        ISqlDatabaseProvider databaseProvider,
        IKeyspace schema,
        VaultDocument document)
        : this(databaseProvider, schema, document, QueryState.Empty)
    {
    }

    /// <summary>Creates a vault carrying an existing query state (used by the fluent operators).</summary>
    /// <param name="databaseProvider">Provider that executes the generated SQL.</param>
    /// <param name="schema">Postgres schema (keyspace) holding the table.</param>
    /// <param name="document">Table metadata for <typeparamref name="TVaultModel"/>.</param>
    /// <param name="state">Accumulated WHERE / ORDER BY / LIMIT / OFFSET / SELECT fragments.</param>
    protected PgVault(
        ISqlDatabaseProvider databaseProvider,
        IKeyspace schema,
        VaultDocument document,
        QueryState state)
        : base(databaseProvider, schema, document, state)
    {
    }

    /// <summary>The query state of this instance (filters, sort keys, paging), for the join layer.</summary>
    internal QueryState State => _state;

    /// <summary>The quoted <c>"schema"."table"</c> name, for the join layer.</summary>
    internal string TableSql => QualifiedTableName();

    /// <summary><c>SELECT * FROM table</c> with this instance's filters, sort keys and paging, for the join layer.</summary>
    internal string FilteredRowsSql()
        => BuildSelectQuery(_state, "*");

    /// <inheritdoc/>
    protected override SqlVault<TVaultModel> Create(QueryState state)
        => new PgVault<TVaultModel>(_databaseProvider, Keyspace, VaultDocument, state);

    /// <inheritdoc/>
    protected override IHistoricalVault<TVaultModel> CreateHistoryVault()
        => new PgHistoricalVault<TVaultModel>(this);

    // ------------------------ Translation ------------------------

    /// <inheritdoc/>
    protected override string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate)
        => PgQueryTranslator.Where(predicate, VaultDocument);

    /// <inheritdoc/>
    protected override string ConvertOrderByToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
        => PgQueryTranslator.OrderBy(keySelector, VaultDocument);

    /// <inheritdoc/>
    /// <remarks>Returns the same fragment as the ascending form; the base vault appends <c>DESC</c>.</remarks>
    protected override string ConvertOrderByDescendingToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
        => PgQueryTranslator.OrderBy(keySelector, VaultDocument);

    /// <inheritdoc/>
    protected override IEnumerable<string> TranslateSelect<TResult>(Expression<Func<TVaultModel, TResult>> selector)
        => PgQueryTranslator.Select(selector, VaultDocument);

    /// <inheritdoc/>
    protected override string QuoteIdent(string ident)
        => $"\"{ident.Replace("\"", "\"\"")}\"";

    // ------------------------ Upsert dialect (Versioned) ------------------------

    /// <inheritdoc/>
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

        var setSql = BuildSetClauses(columns, conflictKeyColumns, versionCol, storageIdCol, CreatedAtColumn(), alias);

        return
            $"INSERT INTO {qualifiedTable} AS {alias} ({colSql}) VALUES ({valsSql}) " +
            $"{conflictSql} DO UPDATE SET {setSql} " +
            $"WHERE {alias}.\"{versionCol}\" = EXCLUDED.\"{versionCol}\" " +
            $"RETURNING {alias}.\"{storageIdCol}\" AS \"{StorageIdLogical}\", {alias}.\"{versionCol}\" AS \"{VersionLogical}\"";
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Single statement: locks the existing rows matching the conflict key (<c>FOR UPDATE</c>), and if any of them
    /// has a different version inserts nothing, so fewer rows are returned than were sent and the caller throws
    /// <see cref="OptimisticConcurrencyException"/>. Each input row carries its 0-based position, and every written row
    /// is joined back to its input row on the conflict key to return that position as <c>Ordinal</c>. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for <paramref name="rowCount"/> &lt;= 0 and <see cref="ArgumentException"/>
    /// for an empty <paramref name="conflictKeyColumns"/>.
    /// </remarks>
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

        const string alias = "t";
        const string inputAlias = "v";
        const string ordinalCol = "__ordinal";

        var versionCol = VersionColumn();
        var storageIdCol = StorageIdColumn();

        // (?, ?, ..., <ordinal>) per row: the ordinal is an inline literal, the values are parameters.
        var placeholders = string.Join(", ", columns.Select(_ => "?"));
        var valuesSql = string.Join(", ", Enumerable.Range(0, rowCount).Select(i => $"({placeholders}, {i})"));

        var colListSql = string.Join(", ", columns.Select(QuoteIdent));
        var keyJoin = string.Join(" AND ",
            conflictKeyColumns.Select(k => $"{alias}.{QuoteIdent(k)} = {inputAlias}.{QuoteIdent(k)}"));

        string conflictSql = !string.IsNullOrWhiteSpace(conflictConstraintName)
            ? $"ON CONFLICT ON CONSTRAINT {QuoteIdent(conflictConstraintName)}"
            : $"ON CONFLICT ({string.Join(", ", conflictKeyColumns.Select(QuoteIdent))})";

        var setSql = BuildSetClauses(columns, conflictKeyColumns, versionCol, storageIdCol, CreatedAtColumn(), alias);

        // A written row is matched back to its input row (and so its ordinal) by id when it was inserted, or by the
        // conflict key when an existing row was updated (its stored id may differ when the key is a unique key).
        var returnedKeys = string.Join(", ", conflictKeyColumns.Select((k, i) => $"{alias}.{QuoteIdent(k)} AS \"__key{i}\""));
        var keyMatch = $"u.\"{StorageIdLogical}\" = i.{QuoteIdent(storageIdCol)} OR (" + string.Join(" AND ",
            conflictKeyColumns.Select((k, i) => $"u.\"__key{i}\" = i.{QuoteIdent(k)}")) + ")";

        // Atomic strategy:
        // - lock existing rows matched by the conflict key
        // - if ANY mismatch -> mismatch has row -> INSERT is skipped -> upserted returns 0 rows
        // - caller detects count != rowCount and throws OptimisticConcurrencyException
        return
$@"
WITH input AS (
    SELECT * FROM (VALUES {valuesSql}) AS {inputAlias}({colListSql}, ""{ordinalCol}"")
),
locked AS (
    SELECT {alias}.{QuoteIdent(versionCol)}
    FROM {qualifiedTable} AS {alias}
    JOIN input AS {inputAlias} ON {keyJoin}
    FOR UPDATE
),
mismatch AS (
    SELECT 1
    FROM {qualifiedTable} AS {alias}
    JOIN input AS {inputAlias} ON {keyJoin}
    WHERE {alias}.{QuoteIdent(versionCol)} <> {inputAlias}.{QuoteIdent(versionCol)}
    LIMIT 1
),
upserted AS (
    INSERT INTO {qualifiedTable} AS {alias} ({colListSql})
    SELECT {colListSql}
    FROM input
    WHERE NOT EXISTS (SELECT 1 FROM mismatch)
    {conflictSql} DO UPDATE
        SET {setSql}
        WHERE {alias}.{QuoteIdent(versionCol)} = EXCLUDED.{QuoteIdent(versionCol)}
    RETURNING {alias}.{QuoteIdent(storageIdCol)} AS ""{StorageIdLogical}"",
              {alias}.{QuoteIdent(versionCol)} AS ""{VersionLogical}"",
              {returnedKeys}
)
SELECT u.""{StorageIdLogical}"", u.""{VersionLogical}"", i.""{ordinalCol}"" AS ""Ordinal""
FROM upserted u
JOIN input i ON {keyMatch}
;";
    }

    private string BuildSetClauses(
        IReadOnlyList<string> columns,
        IReadOnlyList<string> conflictKeyColumns,
        string versionCol,
        string storageIdCol,
        string? createdAtCol,
        string tableAlias)
    {
        var conflictSet = new HashSet<string>(conflictKeyColumns, StringComparer.OrdinalIgnoreCase);

        var sets = new List<string>(columns.Count);

        foreach (var c in columns)
        {
            // never update conflict key columns, the version (bumped below), the storage id (PK, even when the
            // conflict key is a unique key) or the creation time
            if (conflictSet.Contains(c) ||
                string.Equals(c, versionCol, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c, storageIdCol, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c, createdAtCol, StringComparison.OrdinalIgnoreCase))
                continue;

            sets.Add($"{QuoteIdent(c)} = EXCLUDED.{QuoteIdent(c)}");
        }

        sets.Add($"{QuoteIdent(versionCol)} = {tableAlias}.{QuoteIdent(versionCol)} + 1");

        return string.Join(", ", sets);
    }
}
