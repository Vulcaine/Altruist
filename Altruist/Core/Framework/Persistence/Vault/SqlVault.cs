/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Linq.Expressions;
using System.Reflection;

using Microsoft.EntityFrameworkCore.Query;

namespace Altruist.Persistence;

/// <summary>
/// Base vault for SQL providers. Contains all provider-agnostic SQL vault behavior:
/// fluent query state, select building, save batching, history, and Version-based optimistic concurrency.
/// Provider-specific vaults implement translation + dialect-specific upsert SQL.
/// </summary>
/// <remarks>
/// <para>
/// Application code does not use this type directly: inject <see cref="IVault{TVaultModel}"/> (registered as a
/// singleton per <see cref="Altruist.UORM.VaultAttribute"/> model by the provider configuration). Derive from it
/// only when writing a new SQL provider (e.g. Postgres' <c>PgVault</c>).
/// </para>
/// <para>
/// Instances are immutable query builders: each fluent call (<see cref="Where"/>, <see cref="OrderBy{TKey}"/>,
/// <see cref="Take"/>, ...) returns a new vault with an extended <see cref="QueryState"/>, so the shared singleton is
/// safe to use concurrently. Predicate values are rendered into the SQL text by the provider's translator (no
/// bind parameters for queries); saves use positional <c>?</c> parameters.
/// </para>
/// <para>
/// Saves are versioned upserts: <see cref="IVaultModel.OnSave"/> is called first, a version &lt;= 0 becomes 1, the
/// update only applies when the stored version equals the entity's, and the returned StorageId/Version are written
/// back onto the entity. A mismatch throws <see cref="OptimisticConcurrencyException"/>.
/// </para>
/// </remarks>
/// <typeparam name="TVaultModel">The vault model type.</typeparam>
public abstract class SqlVault<TVaultModel> : IVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    #region Versioning (framework guarantees Version column exists)
    /// <summary>Logical (CLR) property name of the storage id.</summary>
    protected const string StorageIdLogical = nameof(IVaultModel.StorageId);
    /// <summary>Logical (CLR) property name of the optimistic-concurrency version.</summary>
    protected const string VersionLogical = "Version"; // matches your VaultModel property name

    /// <summary>Physical column of the storage id (falls back to the logical name when unmapped).</summary>
    protected string StorageIdColumn()
        => VaultDocument.Columns.TryGetValue(StorageIdLogical, out var col)
            ? col
            : StorageIdLogical;

    /// <summary>Physical column of the version (falls back to <c>version</c> when unmapped).</summary>
    protected string VersionColumn()
        => VaultDocument.Columns.TryGetValue(VersionLogical, out var col)
            ? col
            : "version";

    /// <summary>Reads the entity's <c>Version</c> property via reflection; 0 when absent or null.</summary>
    /// <param name="entity">The model instance.</param>
    /// <returns>The current version.</returns>
    protected static long GetVersionValue(object entity)
    {
        var p = entity.GetType().GetProperty(VersionLogical);
        if (p is null)
            return 0;

        var v = p.GetValue(entity);
        if (v is null)
            return 0;

        return (long)v;
    }

    /// <summary>Writes the entity's <c>Version</c> property when it exists, is writable and is <see cref="long"/>.</summary>
    /// <param name="entity">The model instance.</param>
    /// <param name="version">The new version.</param>
    protected static void SetVersionValue(object entity, long version)
    {
        var p = entity.GetType().GetProperty(VersionLogical);
        if (p is null || !p.CanWrite)
            return;

        if (p.PropertyType == typeof(long))
            p.SetValue(entity, version);
    }

    /// <summary>Sets the version to 1 when it is 0 or negative (new entity).</summary>
    /// <param name="entity">The model instance.</param>
    protected static void EnsureInsertVersion(object entity)
    {
        var current = GetVersionValue(entity);
        if (current <= 0)
            SetVersionValue(entity, 1);
    }

    /// <summary>Writes the entity's <c>StorageId</c> when it exists, is writable and is a string.</summary>
    /// <param name="entity">The model instance.</param>
    /// <param name="storageId">The id to write.</param>
    protected static void SetStorageIdValue(object entity, string storageId)
    {
        var p = entity.GetType().GetProperty(StorageIdLogical);
        if (p is null || !p.CanWrite)
            return;

        if (p.PropertyType == typeof(string))
            p.SetValue(entity, storageId);
    }

    /// <summary>
    /// Physical column of <see cref="IVaultModel.Timestamp"/> (the creation time), or <c>null</c> when unmapped. Upserts
    /// never overwrite it on update.
    /// </summary>
    protected string? CreatedAtColumn()
        => VaultDocument.Columns.TryGetValue(nameof(IVaultModel.Timestamp), out var col) ? col : null;

    /// <summary>Row shape returned by the versioned upsert SQL (<c>RETURNING</c> StorageId, Version).</summary>
    protected sealed class UpsertReturnRow
    {
        /// <summary>Storage id of the written row.</summary>
        public string StorageId { get; set; } = string.Empty;
        /// <summary>Version of the written row after the upsert.</summary>
        public long Version { get; set; }
        /// <summary>Batch upserts only: 0-based position of the entity this row was written for.</summary>
        public int Ordinal { get; set; }
    }
    #endregion

    /// <summary>Provider all SQL is executed on (honours an ambient <see cref="SqlAmbientTransaction"/>).</summary>
    protected readonly ISqlDatabaseProvider _databaseProvider;
    /// <summary>The query state accumulated by fluent calls on this instance.</summary>
    protected readonly QueryState _state;

    /// <summary>Schema / keyspace the table lives in.</summary>
    public IKeyspace Keyspace { get; }
    /// <summary>Table metadata (name, column map, keys, history flag) built from the model's attributes.</summary>
    public VaultDocument VaultDocument { get; }

    /// <summary>The database provider this vault executes on; use it for raw SQL that the fluent API cannot express.</summary>
    public ISqlDatabaseProvider DatabaseProvider => _databaseProvider;

    private readonly Lazy<IHistoricalVault<TVaultModel>> _history;

    /// <summary>Query interface over the <c>&lt;table&gt;_history</c> table.</summary>
    /// <exception cref="InvalidOperationException">History is not enabled (<c>StoreHistory</c> is false on the model's <see cref="Altruist.UORM.VaultAttribute"/>).</exception>
    public IHistoricalVault<TVaultModel> History
    {
        get
        {
            if (!VaultDocument.StoreHistory)
                throw new InvalidOperationException("History is not enabled for this document.");

            return _history.Value;
        }
    }

    /// <summary>Creates a vault with an empty query state.</summary>
    /// <param name="databaseProvider">Provider to execute SQL on.</param>
    /// <param name="schema">Schema / keyspace.</param>
    /// <param name="document">Table metadata.</param>
    protected SqlVault(
        ISqlDatabaseProvider databaseProvider,
        IKeyspace schema,
        VaultDocument document)
        : this(databaseProvider, schema, document, QueryState.Empty)
    {
    }

    /// <summary>Creates a vault carrying an existing query state (used by <see cref="Create"/>).</summary>
    /// <param name="databaseProvider">Provider to execute SQL on.</param>
    /// <param name="schema">Schema / keyspace.</param>
    /// <param name="document">Table metadata.</param>
    /// <param name="state">Query state to carry.</param>
    protected SqlVault(
        ISqlDatabaseProvider databaseProvider,
        IKeyspace schema,
        VaultDocument document,
        QueryState state)
    {
        _databaseProvider = databaseProvider ?? throw new ArgumentNullException(nameof(databaseProvider));
        Keyspace = schema ?? throw new ArgumentNullException(nameof(schema));
        VaultDocument = document ?? throw new ArgumentNullException(nameof(document));
        _state = state ?? throw new ArgumentNullException(nameof(state));

        _history = new Lazy<IHistoricalVault<TVaultModel>>(CreateHistoryVault);
    }

    /// <summary>Provider creates a new vault instance with new query state.</summary>
    protected abstract SqlVault<TVaultModel> Create(QueryState state);
    /// <summary>Alias for <see cref="Create"/>.</summary>
    /// <param name="state">Query state for the new instance.</param>
    /// <returns>A new vault.</returns>
    protected SqlVault<TVaultModel> New(QueryState state) => Create(state);

    /// <summary>Provider-specific history vault creation (only used if StoreHistory=true).</summary>
    protected virtual IHistoricalVault<TVaultModel> CreateHistoryVault()
        => throw new NotSupportedException("History vault is not supported by this provider.");

    // ------------------------ Provider hooks (translation / dialect) ------------------------

    /// <summary>Translates a predicate to a SQL boolean expression (values inlined as literals).</summary>
    /// <param name="predicate">The predicate.</param>
    /// <returns>SQL fragment without the WHERE keyword.</returns>
    protected abstract string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate);
    /// <summary>Translates an ascending order key to a SQL expression.</summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="keySelector">The key selector.</param>
    /// <returns>SQL fragment without ORDER BY.</returns>
    protected abstract string ConvertOrderByToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);
    /// <summary>Translates a descending order key to a SQL expression (the base appends <c>DESC</c>).</summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="keySelector">The key selector.</param>
    /// <returns>SQL fragment without ORDER BY or DESC.</returns>
    protected abstract string ConvertOrderByDescendingToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);

    /// <summary>Translates a projection into SELECT list entries.</summary>
    /// <typeparam name="TResult">Projected type.</typeparam>
    /// <param name="selector">The projection.</param>
    /// <returns>SELECT list items.</returns>
    protected abstract IEnumerable<string> TranslateSelect<TResult>(
        Expression<Func<TVaultModel, TResult>> selector)
        where TResult : class, IVaultModel;

    /// <summary>
    /// Versioned upsert for a single row. MUST:
    /// - set version = version+1 on update
    /// - only update if current version == excluded version
    /// - RETURN StorageId + Version
    /// - not overwrite the storage id or <see cref="CreatedAtColumn"/> on update
    ///
    /// conflictKeyColumns:
    /// - either PK columns (default)
    /// - OR unique key columns if present
    ///
    /// conflictConstraintName:
    /// - if not null, provider should use ON CONFLICT ON CONSTRAINT
    /// </summary>
    protected abstract string BuildUpsertSql_VersionedReturning(
        string qualifiedTable,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> conflictKeyColumns,
        string? conflictConstraintName);

    /// <summary>
    /// Versioned batch upsert for N rows. MUST be atomic (no partial writes) and RETURN, for each row, StorageId +
    /// Version + Ordinal (the 0-based position of the entity in the batch, so the result can be matched to its entity
    /// even when the conflict target is a unique key and the stored StorageId differs), or return fewer rows when any
    /// stored version differs. Must not overwrite <see cref="CreatedAtColumn"/> on update.
    /// </summary>
    protected abstract string BuildBatchUpsertSql_VersionedReturning(
        string qualifiedTable,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> conflictKeyColumns,
        string? conflictConstraintName,
        int rowCount);

    /// <summary>Quotes an identifier in the provider's dialect.</summary>
    /// <param name="ident">Identifier to quote.</param>
    /// <returns>The quoted identifier.</returns>
    protected abstract string QuoteIdent(string ident);

    // ------------------------ Fluent query ops (return NEW instance) ------------------------

    /// <summary>Returns a new vault with an extra filter; several calls are combined with AND.</summary>
    /// <param name="predicate">Filter on model properties; supported shapes depend on the provider translator.</param>
    /// <returns>A new vault; this instance is unchanged.</returns>
    /// <exception cref="InvalidOperationException">The chain already has <see cref="Skip"/>/<see cref="Take"/>: filter before paging.</exception>
    public IVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
    {
        EnsureNotPaged(nameof(Where));
        return New(_state.WithFilter(ConvertWherePredicateToString(predicate)));
    }

    /// <summary>Returns a new vault that additionally sorts ascending by the key (keys apply in call order).</summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="keySelector">Property to sort by.</param>
    /// <returns>A new vault.</returns>
    /// <exception cref="InvalidOperationException">The chain already has <see cref="Skip"/>/<see cref="Take"/>: sort before paging.</exception>
    public IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        EnsureNotPaged(nameof(OrderBy));
        return New(_state.WithOrderKey(ConvertOrderByToString(keySelector)));
    }

    /// <summary>Returns a new vault that additionally sorts descending by the key.</summary>
    /// <typeparam name="TKey">Key type.</typeparam>
    /// <param name="keySelector">Property to sort by.</param>
    /// <returns>A new vault.</returns>
    /// <exception cref="InvalidOperationException">The chain already has <see cref="Skip"/>/<see cref="Take"/>: sort before paging.</exception>
    public IVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        EnsureNotPaged(nameof(OrderByDescending));
        return New(_state.WithOrderKey(ConvertOrderByDescendingToString(keySelector) + " DESC"));
    }

    /// <summary>
    /// Returns a new vault keeping at most <paramref name="count"/> rows of the current window (SQL <c>LIMIT</c>).
    /// Composes like LINQ: <c>Take(10).Take(3)</c> keeps 3 rows, <c>Take(10).Skip(4)</c> keeps rows 4..9.
    /// </summary>
    /// <param name="count">Maximum number of rows; negative counts as 0.</param>
    /// <returns>A new vault.</returns>
    public IVault<TVaultModel> Take(int count) => New(_state.Take(count));

    /// <summary>
    /// Returns a new vault that skips <paramref name="count"/> rows of the current window (SQL <c>OFFSET</c>); combine
    /// with an OrderBy for stable paging. Composes like LINQ: <c>Skip(2).Skip(3)</c> skips 5 rows.
    /// </summary>
    /// <param name="count">Rows to skip; negative counts as 0.</param>
    /// <returns>A new vault.</returns>
    public IVault<TVaultModel> Skip(int count) => New(_state.Skip(count));

    // ------------------------ Terminal ops (use current state) ------------------------

    /// <summary>Executes the query and returns all matching rows.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The rows (empty when none match).</returns>
    public virtual async Task<List<TVaultModel>> ToListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var rows = await _databaseProvider.QueryAsync<TVaultModel>(
            BuildSelectQuery(_state, FullProjection()), parameters: null, ct).ConfigureAwait(false);
        return rows.ToList();
    }

    /// <summary>Returns the first row of the current window (respecting ordering and paging), or null.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The first row or null.</returns>
    public virtual async Task<TVaultModel?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var rows = await _databaseProvider.QueryAsync<TVaultModel>(
            BuildSelectQuery(_state.Take(1), FullProjection()), parameters: null, ct).ConfigureAwait(false);
        return rows.FirstOrDefault();
    }

    /// <summary>Returns the first row of the current window. Prefer <see cref="FirstOrDefaultAsync"/> when no match is a normal outcome.</summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The first row.</returns>
    /// <exception cref="InvalidOperationException">No row matches.</exception>
    public virtual async Task<TVaultModel?> FirstAsync(CancellationToken ct = default)
        => await FirstOrDefaultAsync(ct).ConfigureAwait(false)
           ?? throw new InvalidOperationException($"No {typeof(TVaultModel).Name} row matches the query.");

    /// <summary>Shortcut for <c>Where(predicate).ToListAsync(ct)</c>.</summary>
    /// <param name="predicate">Extra filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The matching rows.</returns>
    /// <exception cref="InvalidOperationException">The chain already has <see cref="Skip"/>/<see cref="Take"/>.</exception>
    public virtual Task<List<TVaultModel>> ToListAsync(
        Expression<Func<TVaultModel, bool>> predicate,
        CancellationToken ct = default)
        => Where(predicate).ToListAsync(ct);

    /// <summary>
    /// Counts the rows the query would return: the rows matching the filters, limited to the
    /// <see cref="Skip"/>/<see cref="Take"/> window when the chain is paged (<c>SELECT COUNT(*)</c>).
    /// </summary>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Row count.</returns>
    public virtual async Task<long> CountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _databaseProvider.ExecuteCountAsync(CountQuery(_state), parameters: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Executes the query selecting only the columns the projection reads (instead of the full column list).
    /// </summary>
    /// <typeparam name="TResult">Projected vault model type.</typeparam>
    /// <param name="selector">The projection, e.g. <c>x =&gt; new TResult { A = x.A }</c>; translated by the provider.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The projected rows; members not assigned by the projection keep their defaults.</returns>
    public virtual async Task<IEnumerable<TResult>> SelectAsync<TResult>(
        Expression<Func<TVaultModel, TResult>> selector,
        CancellationToken ct = default)
        where TResult : class, IVaultModel
    {
        ct.ThrowIfCancellationRequested();
        var projection = string.Join(", ", TranslateSelect(selector));
        return await _databaseProvider.QueryAsync<TResult>(
            BuildSelectQuery(_state, projection), parameters: null, ct).ConfigureAwait(false);
    }

    /// <summary>Returns whether any row matches the current filters plus <paramref name="predicate"/>.</summary>
    /// <param name="predicate">Extra filter.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when at least one row matches.</returns>
    /// <exception cref="InvalidOperationException">The chain already has <see cref="Skip"/>/<see cref="Take"/>.</exception>
    public virtual async Task<bool> AnyAsync(
        Expression<Func<TVaultModel, bool>> predicate,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var next = (SqlVault<TVaultModel>)Where(predicate);
        var count = await _databaseProvider.ExecuteCountAsync(
            CountQuery(next._state.Take(1)), parameters: null, ct).ConfigureAwait(false);
        return count > 0;
    }

    // ------------------------ Update / Delete ------------------------

    /// <summary>
    /// Updates matching rows: loads them (respecting Where/OrderBy/Take/Skip), applies the <c>SetProperty</c>
    /// assignments in memory, then saves them with one versioned batch upsert (no history).
    /// </summary>
    /// <remarks>
    /// Not a single SQL UPDATE: every matching row is loaded and re-saved through <see cref="SaveBatchAsync"/>, so
    /// <see cref="IVaultModel.OnSave"/> runs and each row's version increases. A concurrent change to any of them makes
    /// the whole batch fail with <see cref="OptimisticConcurrencyException"/>. For large sets prefer raw SQL through
    /// <see cref="DatabaseProvider"/>.
    /// </remarks>
    /// <example>
    /// <code>
    /// await vault.Where(x =&gt; x.Level &lt; 5)
    ///            .UpdateAsync(s =&gt; s.SetProperty(x =&gt; x.Level, x =&gt; 5));
    /// </code>
    /// </example>
    /// <param name="setPropertyCalls">EF Core style <c>SetProperty(property, value)</c> chain.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of rows updated.</returns>
    /// <exception cref="NotSupportedException">The expression contains no recognisable assignments.</exception>
    public virtual async Task<long> UpdateAsync(
        Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var assigns = ParseSetPropertyCalls(setPropertyCalls);
        var targets = await ToListAsync(ct).ConfigureAwait(false);
        if (targets.Count == 0)
            return 0;

        foreach (var e in targets)
            ApplyAssignments(e, assigns);

        await SaveBatchAsync(targets, saveHistory: false, ct).ConfigureAwait(false);
        return targets.Count;
    }

    /// <summary>
    /// Deletes the rows the query selects: the rows matching the filters, limited to the
    /// <see cref="Skip"/>/<see cref="Take"/> window (in <see cref="OrderBy{TKey}"/> order) when the chain is paged.
    /// </summary>
    /// <remarks>
    /// A chain with neither a filter nor paging would delete the whole table; that throws instead. Call
    /// <see cref="DeleteAllAsync"/> to empty a table on purpose.
    /// </remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when at least one row was deleted.</returns>
    /// <exception cref="InvalidOperationException">The chain has no <see cref="Where"/> and no <see cref="Skip"/>/<see cref="Take"/>.</exception>
    public virtual async Task<bool> DeleteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        if (_state.Filters.Count == 0 && !_state.IsPaged)
            throw new InvalidOperationException(
                $"DeleteAsync without a Where filter would delete every {typeof(TVaultModel).Name} row. " +
                "Add a Where (or Take) to the chain, or call DeleteAllAsync to empty the table on purpose.");

        var affected = await _databaseProvider.ExecuteAsync(DeleteQuery(_state), parameters: null, ct).ConfigureAwait(false);
        return affected > 0;
    }

    /// <summary>Deletes every row of the table, ignoring this chain's filters and paging (<c>DELETE FROM table</c>).</summary>
    /// <remarks>Use it only to empty a table on purpose; <see cref="DeleteAsync"/> refuses to run without a filter.</remarks>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>Number of deleted rows.</returns>
    public virtual async Task<long> DeleteAllAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return await _databaseProvider.ExecuteAsync($"DELETE FROM {QualifiedTableName()}", parameters: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Opens a cursor that reads the query's rows in batches of <see cref="CursorBatchSize"/> (each batch is one
    /// <c>LIMIT</c>/<c>OFFSET</c> query, so nothing stays open between batches).
    /// </summary>
    /// <remarks>
    /// An unpaged chain gets the primary key appended as the final sort key, so batches neither repeat nor skip rows
    /// as long as the table is not modified while reading. A paged chain is read within its window as written. Prefer
    /// <see cref="ICursor{T}.NextBatch"/> in a <c>while (cursor.HasNext)</c> loop: <c>foreach</c> blocks on each batch.
    /// </remarks>
    /// <param name="ct">Cancellation token, observed by every batch.</param>
    /// <returns>The cursor (no query runs until the first batch).</returns>
    public virtual Task<ICursor<TVaultModel>> ToCursorAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var state = _state;
        if (!state.IsPaged)
        {
            foreach (var key in PrimaryKeyColumns().Select(QuoteIdent).Where(k => !state.OrderKeys.Contains(k)))
                state = state.WithOrderKey(key);
        }

        var projection = FullProjection();
        ICursor<TVaultModel> cursor = new SqlVaultCursor<TVaultModel>(
            async (offset, count, token) => (await _databaseProvider.QueryAsync<TVaultModel>(
                BuildSelectQuery(state.Skip(offset).Take(count), projection), parameters: null, token)
                .ConfigureAwait(false)).ToList(),
            CursorBatchSize,
            ct);
        return Task.FromResult(cursor);
    }

    /// <summary>Rows per batch of <see cref="ToCursorAsync"/>.</summary>
    public const int CursorBatchSize = 500;

    // ------------------------ Save ------------------------

    /// <summary>
    /// Inserts or updates one entity with a versioned upsert, then writes the stored StorageId and Version back onto it.
    /// </summary>
    /// <remarks>
    /// Calls <see cref="IVaultModel.OnSave"/> first (on <see cref="VaultModel"/>: assigns an id and the creation time when
    /// they are unset, stamps the type). The conflict target is the primary key, or the first
    /// <see cref="Altruist.UORM.VaultUniqueKeyAttribute"/> when one exists; an update keeps the stored id and creation time.
    /// Runs inside the ambient transaction when one is active. For many entities prefer <see cref="SaveBatchAsync"/>.
    /// </remarks>
    /// <param name="entity">The entity to save.</param>
    /// <param name="saveHistory">When true, also appends a timestamped copy to the history table (requires <c>StoreHistory</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="OptimisticConcurrencyException">The stored version differs from the entity's version.</exception>
    /// <exception cref="InvalidOperationException"><paramref name="saveHistory"/> is true but history is not enabled.</exception>
    public virtual Task SaveAsync(TVaultModel entity, bool? saveHistory = false, CancellationToken ct = default)
        => SaveEntityAsync(entity, saveHistory, ct);

    /// <summary>
    /// Upserts many entities in one atomic statement (all or nothing) and writes each stored StorageId and Version back
    /// onto its entity, like <see cref="SaveAsync"/> (also when the conflict target is a unique key and the stored row
    /// has a different id). An empty sequence does nothing.
    /// </summary>
    /// <remarks>
    /// Only a version mismatch is reported as <see cref="OptimisticConcurrencyException"/>; any other failure (connection,
    /// constraint, SQL) propagates unchanged.
    /// </remarks>
    /// <param name="entities">The entities.</param>
    /// <param name="saveHistory">When true, also appends history rows (requires <c>StoreHistory</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="OptimisticConcurrencyException">A stored version differs from its entity's; nothing was written.</exception>
    public virtual Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false, CancellationToken ct = default)
        => SaveEntitiesAsync(entities, saveHistory, ct);

    // ------------------------ Query building helpers ------------------------

    /// <summary>Every mapped column as <c>"column" AS "Property"</c>, comma-separated.</summary>
    /// <returns>The SELECT list.</returns>
    protected string FullProjection()
        => string.Join(", ", VaultDocument.Columns.Select(kvp => $"{QuoteIdent(kvp.Value)} AS {QuoteIdent(kvp.Key)}"));

    /// <summary>Physical primary-key columns (the logical key names mapped through the document).</summary>
    /// <returns>The columns, unquoted.</returns>
    /// <exception cref="InvalidOperationException">The model declares no primary key.</exception>
    protected IReadOnlyList<string> PrimaryKeyColumns()
    {
        var keys = VaultDocument.PrimaryKey?.Keys;
        if (keys is null || keys.Length == 0)
            throw new InvalidOperationException($"{typeof(TVaultModel).Name} declares no primary key.");
        return keys.Select(k => VaultDocument.Columns.TryGetValue(k, out var col) ? col : k).ToArray();
    }

    /// <summary>Builds <c>SELECT projection FROM table [WHERE] [ORDER BY] [LIMIT] [OFFSET]</c> for a query state.</summary>
    /// <param name="st">The query state.</param>
    /// <param name="projection">The SELECT list.</param>
    /// <returns>SQL text.</returns>
    protected virtual string BuildSelectQuery(QueryState st, string projection)
        => $"SELECT {projection} FROM {QualifiedTableName()}{st.WhereClause()}{st.OrderByClause()}{st.PagingClause()}";

    /// <summary><c>SELECT COUNT(*)</c> over the rows <paramref name="st"/> selects (its paging window included).</summary>
    /// <param name="st">The query state.</param>
    /// <returns>SQL text.</returns>
    protected string CountQuery(QueryState st)
        => st.IsPaged
            ? $"SELECT COUNT(*) FROM ({BuildSelectQuery(st, "1")}) AS q"
            : $"SELECT COUNT(*) FROM {QualifiedTableName()}{st.WhereClause()}";

    /// <summary>
    /// <c>DELETE</c> of the rows <paramref name="st"/> selects; a paged state deletes by primary key from the window
    /// sub-query (SQL <c>DELETE</c> has no <c>LIMIT</c>).
    /// </summary>
    /// <param name="st">The query state.</param>
    /// <returns>SQL text.</returns>
    protected string DeleteQuery(QueryState st)
    {
        if (!st.IsPaged)
            return $"DELETE FROM {QualifiedTableName()}{st.WhereClause()}";

        var keys = string.Join(", ", PrimaryKeyColumns().Select(QuoteIdent));
        return $"DELETE FROM {QualifiedTableName()} WHERE ({keys}) IN ({BuildSelectQuery(st, keys)})";
    }

    /// <summary>Returns the quoted <c>"keyspace"."table"</c> name.</summary>
    /// <returns>Qualified table name.</returns>
    protected virtual string QualifiedTableName()
        => $"{QuoteIdent(Keyspace.Name)}.{QuoteIdent(VaultDocument.Name)}";

    private void EnsureNotPaged(string operation)
    {
        if (_state.IsPaged)
            throw new InvalidOperationException(
                $"{operation} after Skip/Take is not supported: SQL filters and sorts before paging. " +
                $"Call {operation} before Skip/Take.");
    }

    // ------------------------ Save helpers ------------------------

    private async Task SaveEntityAsync(TVaultModel entity, bool? saveHistory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (entity is null)
            throw new ArgumentNullException(nameof(entity));

        entity.OnSave();
        EnsureInsertVersion(entity);

        var fields = VaultDocument.Fields.ToArray();
        var columns = fields.Select(f => VaultDocument.Columns[f]).ToArray();

        var pkKeys = VaultDocument.PrimaryKey?.Keys ?? Array.Empty<string>();
        var primaryKeyColumns = pkKeys
            .Select(k => VaultDocument.Columns.TryGetValue(k, out var col) ? col : k)
            .ToArray();

        IReadOnlyList<string> conflictColumns = primaryKeyColumns;
        string? conflictConstraintName = null;

        if (VaultDocument.UniqueKeys is not null && VaultDocument.UniqueKeys.Count > 0)
        {
            conflictColumns = VaultDocument.UniqueKeys[0].Columns.ToArray();
            conflictConstraintName = ConstraintUtil.GetUniqueConstraintName(VaultDocument.Name, conflictColumns);
        }

        var upsertV = BuildUpsertSql_VersionedReturning(
            QualifiedTableName(),
            columns,
            conflictColumns,
            conflictConstraintName);

        var parmsV = GetParameterValues(entity, fields, includeTimestamp: false);

        var returned = await _databaseProvider.QueryAsync<UpsertReturnRow>(
            upsertV,
            parmsV,
            ct).ConfigureAwait(false);

        var row = returned.FirstOrDefault();
        if (row is null)
        {
            throw new OptimisticConcurrencyException(
                typeof(TVaultModel),
                entity.StorageId,
                $"Optimistic concurrency failure saving {typeof(TVaultModel).Name} (StorageId={entity.StorageId}). Version mismatch.");
        }

        // If conflict is a UNIQUE constraint, the row's StorageId may not match the entity's StorageId.
        // Always sync StorageId + Version back.
        if (!string.IsNullOrWhiteSpace(row.StorageId))
            SetStorageIdValue(entity, row.StorageId);

        SetVersionValue(entity, row.Version);

        if (saveHistory == true)
        {
            if (VaultDocument.StoreHistory != true)
                throw new InvalidOperationException(
                    $"History is not enabled for the table {VaultDocument.Name}. Consider enabling StoreHistory=true.");

            var historyQuery = BuildHistoryQuery(columns);
            var historyParams = GetParameterValues(entity, fields, includeTimestamp: true);
            await _databaseProvider.ExecuteAsync(historyQuery + ";", historyParams, ct).ConfigureAwait(false);
        }
    }

    private async Task SaveEntitiesAsync(IEnumerable<TVaultModel> entities, bool? saveHistory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (entities is null)
            throw new ArgumentNullException(nameof(entities));

        var list = entities as IList<TVaultModel> ?? entities.ToList();
        if (list.Count == 0)
            return;

        foreach (var e in list)
        {
            ct.ThrowIfCancellationRequested();
            e.OnSave();
            EnsureInsertVersion(e);
        }

        var fields = VaultDocument.Fields.ToArray();
        var columns = fields.Select(f => VaultDocument.Columns[f]).ToArray();

        var pkKeys = VaultDocument.PrimaryKey?.Keys ?? Array.Empty<string>();
        var primaryKeyColumns = pkKeys
            .Select(k => VaultDocument.Columns.TryGetValue(k, out var col) ? col : k)
            .ToArray();

        IReadOnlyList<string> conflictColumns = primaryKeyColumns;
        string? conflictConstraintName = null;

        if (VaultDocument.UniqueKeys is not null && VaultDocument.UniqueKeys.Count > 0)
        {
            conflictColumns = VaultDocument.UniqueKeys[0].Columns.ToArray();
            conflictConstraintName = ConstraintUtil.GetUniqueConstraintName(VaultDocument.Name, conflictColumns);
        }

        var batchSqlV = BuildBatchUpsertSql_VersionedReturning(
            QualifiedTableName(),
            columns,
            conflictColumns,
            conflictConstraintName,
            list.Count);

        var batchParams = new List<object?>(capacity: list.Count * fields.Length);
        foreach (var e in list)
            batchParams.AddRange(GetParameterValues(e, fields, includeTimestamp: false));

        var returnedRows = (await _databaseProvider.QueryAsync<UpsertReturnRow>(
            batchSqlV,
            batchParams,
            ct).ConfigureAwait(false)).ToList();

        if (returnedRows.Count != list.Count)
        {
            throw new OptimisticConcurrencyException(
                typeof(TVaultModel),
                storageId: null,
                message: $"Optimistic concurrency failure saving batch of {typeof(TVaultModel).Name}. Expected {list.Count} rows, got {returnedRows.Count}.",
                expectedAffected: list.Count,
                actualAffected: returnedRows.Count);
        }

        foreach (var row in returnedRows)
        {
            var e = list[row.Ordinal];
            if (!string.IsNullOrWhiteSpace(row.StorageId))
                SetStorageIdValue(e, row.StorageId);
            SetVersionValue(e, row.Version);
        }

        if (saveHistory == true)
        {
            if (VaultDocument.StoreHistory != true)
                throw new InvalidOperationException(
                    $"History is not enabled for the table {VaultDocument.Name}. Consider enabling StoreHistory=true.");

            var historyQuery = BuildHistoryQuery(columns);
            var historyStatements = new List<string>(list.Count);
            var historyParamsAll = new List<object?>(capacity: list.Count * (fields.Length + 1));

            foreach (var e in list)
            {
                historyStatements.Add(historyQuery);
                historyParamsAll.AddRange(GetParameterValues(e, fields, includeTimestamp: true));
            }

            var batchHistorySql = string.Join(";", historyStatements) + ";";
            await _databaseProvider.ExecuteAsync(batchHistorySql, historyParamsAll, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Builds the INSERT into <c>&lt;table&gt;_history</c> with the given columns plus <c>timestamp</c>, using <c>?</c> placeholders.</summary>
    /// <param name="columns">Physical column names.</param>
    /// <returns>SQL text without trailing semicolon.</returns>
    protected virtual string BuildHistoryQuery(IReadOnlyList<string> columns)
    {
        var histQualified = $"{QuoteIdent(Keyspace.Name)}.{QuoteIdent(VaultDocument.Name + "_history")}";

        var cols = string.Join(", ", columns.Select(QuoteIdent));
        var vals = string.Join(", ", columns.Select(_ => "?"));
        return $"INSERT INTO {histQualified} ({cols}, {QuoteIdent("timestamp")}) VALUES ({vals}, ?)";
    }

    /// <summary>Reads the entity's values for <paramref name="fields"/> in order, optionally appending <see cref="DateTime.UtcNow"/>.</summary>
    /// <param name="entity">The entity.</param>
    /// <param name="fields">Logical field names.</param>
    /// <param name="includeTimestamp">Append the current UTC time (history inserts).</param>
    /// <returns>Positional parameter values.</returns>
    protected List<object?> GetParameterValues(TVaultModel entity, IReadOnlyList<string> fields, bool includeTimestamp)
    {
        var values = fields.Select(field => VaultDocument.PropertyAccessors[field](entity)).ToList();

        if (includeTimestamp)
            values.Add(DateTime.UtcNow);

        return values;
    }

    // ------------------------ Update parsing (SetPropertyCalls -> assignments) ------------------------

    private sealed record Assignment(PropertyInfo Property, LambdaExpression ValueSelector);

    private static List<Assignment> ParseSetPropertyCalls(
        Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> expr)
    {
        var assigns = new List<Assignment>();
        Expression? cur = expr.Body;

        while (cur is MethodCallExpression mc)
        {
            if (mc.Arguments.Count >= 3)
            {
                var propArg = Unquote(mc.Arguments[1]) as LambdaExpression;
                var valArg = Unquote(mc.Arguments[2]) as LambdaExpression;

                if (propArg is not null && valArg is not null)
                {
                    var prop = ExtractPropertyInfo(propArg);
                    if (prop is not null)
                        assigns.Add(new Assignment(prop, valArg));
                }
            }

            cur = mc.Arguments.Count > 0 ? mc.Arguments[0] : null;
        }

        if (assigns.Count == 0)
            throw new NotSupportedException("Unable to parse SetPropertyCalls expression into property assignments.");

        return assigns;
    }

    private static Expression Unquote(Expression e)
    {
        while (e is UnaryExpression u &&
               (u.NodeType == ExpressionType.Quote || u.NodeType == ExpressionType.Convert))
            e = u.Operand;
        return e;
    }

    private static PropertyInfo? ExtractPropertyInfo(LambdaExpression propSelector)
    {
        var body = Unquote(propSelector.Body);

        if (body is MemberExpression me && me.Member is PropertyInfo pi)
            return pi;

        if (body is UnaryExpression u && u.Operand is MemberExpression me2 && me2.Member is PropertyInfo pi2)
            return pi2;

        return null;
    }

    private static void ApplyAssignments(TVaultModel entity, List<Assignment> assigns)
    {
        for (int i = 0; i < assigns.Count; i++)
        {
            var a = assigns[i];

            var del = a.ValueSelector.Compile();
            var value = del.DynamicInvoke(entity);

            if (value is null)
            {
                if (!a.Property.PropertyType.IsValueType || Nullable.GetUnderlyingType(a.Property.PropertyType) is not null)
                    a.Property.SetValue(entity, null);
                continue;
            }

            var targetType = Nullable.GetUnderlyingType(a.Property.PropertyType) ?? a.Property.PropertyType;
            if (targetType.IsInstanceOfType(value))
            {
                a.Property.SetValue(entity, value);
                continue;
            }

            a.Property.SetValue(entity, Convert.ChangeType(value, targetType));
        }
    }
}
