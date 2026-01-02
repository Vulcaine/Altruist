// SqlVault.cs
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
public abstract class SqlVault<TVaultModel> : IVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    #region Versioning (framework guarantees Version column exists)
    protected const string StorageIdLogical = nameof(IVaultModel.StorageId);
    protected const string VersionLogical = "Version"; // matches your VaultModel property name

    protected string StorageIdColumn()
        => VaultDocument.Columns.TryGetValue(StorageIdLogical, out var col)
            ? col
            : StorageIdLogical;

    protected string VersionColumn()
        => VaultDocument.Columns.TryGetValue(VersionLogical, out var col)
            ? col
            : "version";

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

    protected static void SetVersionValue(object entity, long version)
    {
        var p = entity.GetType().GetProperty(VersionLogical);
        if (p is null || !p.CanWrite)
            return;

        if (p.PropertyType == typeof(long))
            p.SetValue(entity, version);
    }

    protected static void EnsureInsertVersion(object entity)
    {
        var current = GetVersionValue(entity);
        if (current <= 0)
            SetVersionValue(entity, 1);
    }

    protected sealed class UpsertReturnRow
    {
        public string StorageId { get; set; } = string.Empty;
        public long Version { get; set; }
    }
    #endregion

    protected readonly ISqlDatabaseProvider _databaseProvider;
    protected readonly QueryState _state;

    public IKeyspace Keyspace { get; }
    public VaultDocument VaultDocument { get; }

    public ISqlDatabaseProvider DatabaseProvider => _databaseProvider;

    private readonly Lazy<IHistoricalVault<TVaultModel>> _history;

    public IHistoricalVault<TVaultModel> History
    {
        get
        {
            if (!VaultDocument.StoreHistory)
                throw new InvalidOperationException("History is not enabled for this document.");

            return _history.Value;
        }
    }

    protected SqlVault(
        ISqlDatabaseProvider databaseProvider,
        IKeyspace schema,
        VaultDocument document)
        : this(databaseProvider, schema, document, new QueryState())
    {
    }

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
    protected SqlVault<TVaultModel> New(QueryState state) => Create(state);

    /// <summary>Provider-specific history vault creation (only used if StoreHistory=true).</summary>
    protected virtual IHistoricalVault<TVaultModel> CreateHistoryVault()
        => throw new NotSupportedException("History vault is not supported by this provider.");

    // ------------------------ Provider hooks (translation / dialect) ------------------------

    protected abstract string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate);
    protected abstract string ConvertOrderByToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);
    protected abstract string ConvertOrderByDescendingToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);

    protected abstract IEnumerable<string> TranslateSelect<TResult>(
        Expression<Func<TVaultModel, TResult>> selector)
        where TResult : class, IVaultModel;

    /// <summary>
    /// Versioned upsert for a single row. MUST:
    /// - set version = version+1 on update
    /// - only update if current version == excluded version
    /// - RETURN StorageId + Version
    /// </summary>
    protected abstract string BuildUpsertSql_VersionedReturning(
        string qualifiedTable,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> primaryKeyColumns);

    /// <summary>
    /// Versioned batch upsert for N rows. MUST be atomic (no partial writes) and RETURN StorageId + Version
    /// for each row (or fail).
    /// </summary>
    protected abstract string BuildBatchUpsertSql_VersionedReturning(
        string qualifiedTable,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> primaryKeyColumns,
        int rowCount);

    protected abstract string QuoteIdent(string ident);

    // ------------------------ Fluent query ops (return NEW instance) ------------------------

    public IVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
    {
        var whereClause = ConvertWherePredicateToString(predicate);
        var next = _state.With(QueryPosition.WHERE, whereClause)
                         .EnsureProjectionSelected(VaultDocument);
        return New(next);
    }

    public IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        var orderByClause = ConvertOrderByToString(keySelector);
        var next = _state.With(QueryPosition.ORDER_BY, orderByClause)
                         .With(QueryPosition.SELECT, orderByClause);
        return New(next);
    }

    public IVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        var orderByClause = ConvertOrderByDescendingToString(keySelector);
        var next = _state.With(QueryPosition.ORDER_BY, orderByClause + " DESC")
                         .With(QueryPosition.SELECT, orderByClause);
        return New(next);
    }

    public IVault<TVaultModel> Take(int count)
    {
        var next = _state.With(QueryPosition.LIMIT, $"LIMIT {count}")
                         .EnsureProjectionSelected(VaultDocument);
        return New(next);
    }

    public IVault<TVaultModel> Skip(int count)
    {
        var next = _state.With(QueryPosition.OFFSET, $"OFFSET {count}")
                         .EnsureProjectionSelected(VaultDocument);
        return New(next);
    }

    // ------------------------ Terminal ops (use current state) ------------------------

    public virtual async Task<List<TVaultModel>> ToListAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var st = _state.EnsureProjectionSelected(VaultDocument);
        var query = BuildSelectQuery(st);

        var rows = await _databaseProvider.QueryAsync<TVaultModel>(
            query,
            parameters: null,
            ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        return rows.ToList();
    }

    public virtual async Task<TVaultModel?> FirstOrDefaultAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var st = _state.EnsureProjectionSelected(VaultDocument)
                       .With(QueryPosition.LIMIT, "LIMIT 1");
        var query = BuildSelectQuery(st);

        var result = await _databaseProvider.QueryAsync<TVaultModel>(
            query,
            parameters: null,
            ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        return result.FirstOrDefault();
    }

    public virtual async Task<TVaultModel?> FirstAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var st = _state.EnsureProjectionSelected(VaultDocument)
                       .With(QueryPosition.LIMIT, "LIMIT 1");
        var query = BuildSelectQuery(st);

        var result = await _databaseProvider.QueryAsync<TVaultModel>(
            query,
            parameters: null,
            ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        return result.First();
    }

    public virtual async Task<List<TVaultModel>> ToListAsync(
        Expression<Func<TVaultModel, bool>> predicate,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var next = (SqlVault<TVaultModel>)Where(predicate);
        return await next.ToListAsync(ct).ConfigureAwait(false);
    }

    public virtual async Task<long> CountAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var whereClause = string.Join(" AND ", _state.Parts[QueryPosition.WHERE]);
        var from = QualifiedTableName();

        var sql = string.IsNullOrEmpty(whereClause)
            ? $"SELECT COUNT(*) FROM {from}"
            : $"SELECT COUNT(*) FROM {from} WHERE {whereClause}";

        var count = await _databaseProvider.ExecuteCountAsync(
            sql,
            parameters: null,
            ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        return count;
    }

    public virtual async Task<IEnumerable<TResult>> SelectAsync<TResult>(
        Expression<Func<TVaultModel, TResult>> selector,
        CancellationToken ct = default)
        where TResult : class, IVaultModel
    {
        ct.ThrowIfCancellationRequested();

        if (_state.Parts[QueryPosition.SELECT].Contains("*"))
            throw new InvalidOperationException("Invalid query. SELECT * followed by other columns is not allowed.");

        var st = _state;
        foreach (var column in TranslateSelect(selector))
            st = st.With(QueryPosition.SELECT, column);

        var query = BuildSelectQuery(st);

        var result = await _databaseProvider.QueryAsync<TResult>(
            query,
            parameters: null,
            ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        return result;
    }

    public virtual async Task<bool> AnyAsync(
        Expression<Func<TVaultModel, bool>> predicate,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var next = (SqlVault<TVaultModel>)Where(predicate);
        var where = string.Join(" AND ", next._state.Parts[QueryPosition.WHERE]);
        var from = next.QualifiedTableName();

        var query = string.IsNullOrEmpty(where)
            ? $"SELECT COUNT(*) FROM {from}"
            : $"SELECT COUNT(*) FROM {from} WHERE {where}";

        var count = await _databaseProvider.ExecuteCountAsync(
            query,
            parameters: null,
            ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        return count > 0;
    }

    // ------------------------ Update / Delete ------------------------

    /// <summary>
    /// Version-safe update:
    /// - loads targets matching current query state
    /// - applies SetPropertyCalls assignments in memory
    /// - persists via SaveBatchAsync (optimistic concurrency + version bump)
    /// </summary>
    public virtual async Task<long> UpdateAsync(
        Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var targets = await ToListAsync(ct).ConfigureAwait(false);
        if (targets.Count == 0)
            return 0;

        var assigns = ParseSetPropertyCalls(setPropertyCalls);

        foreach (var e in targets)
            ApplyAssignments(e, assigns);

        await SaveBatchAsync(targets, saveHistory: false, ct).ConfigureAwait(false);
        return targets.Count;
    }

    public virtual async Task<bool> DeleteAsync(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var from = QualifiedTableName();
        var whereClause = string.Join(" AND ", _state.Parts[QueryPosition.WHERE]);

        var sql = string.IsNullOrEmpty(whereClause)
            ? $"DELETE FROM {from}"
            : $"DELETE FROM {from} WHERE {whereClause}";

        var affected = await _databaseProvider.ExecuteAsync(
            sql,
            parameters: null,
            ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        return affected > 0;
    }

    public virtual Task<ICursor<TVaultModel>> ToCursorAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    // ------------------------ Save ------------------------

    public virtual Task SaveAsync(TVaultModel entity, bool? saveHistory = false, CancellationToken ct = default)
        => SaveEntityAsync(entity, saveHistory, ct);

    public virtual Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false, CancellationToken ct = default)
        => SaveEntitiesAsync(entities, saveHistory, ct);

    // ------------------------ Query building helpers ------------------------

    protected virtual string BuildSelectQuery(QueryState st)
    {
        var select =
            st.Parts[QueryPosition.SELECT].Count == 0
                ? string.Join(", ",
                    VaultDocument.Columns.Select(kvp => $"{QuoteIdent(kvp.Value)} AS {QuoteIdent(kvp.Key)}"))
                : string.Join(", ", st.Parts[QueryPosition.SELECT]);

        var sb = new System.Text.StringBuilder(256);
        sb.Append("SELECT ").Append(select)
          .Append(" FROM ").Append(QualifiedTableName());

        var where = string.Join(" AND ", st.Parts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(where))
            sb.Append(" WHERE ").Append(where);

        var orderBy = string.Join(", ", st.Parts[QueryPosition.ORDER_BY]);
        if (!string.IsNullOrEmpty(orderBy))
            sb.Append(" ORDER BY ").Append(orderBy);

        var limit = string.Join(" ", st.Parts[QueryPosition.LIMIT]);
        if (!string.IsNullOrEmpty(limit))
            sb.Append(' ').Append(limit);

        var offset = string.Join(" ", st.Parts[QueryPosition.OFFSET]);
        if (!string.IsNullOrEmpty(offset))
            sb.Append(' ').Append(offset);

        return sb.ToString();
    }

    protected virtual string QualifiedTableName()
        => $"{QuoteIdent(Keyspace.Name)}.{QuoteIdent(VaultDocument.Name)}";

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

        var upsertV = BuildUpsertSql_VersionedReturning(QualifiedTableName(), columns, primaryKeyColumns);
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
            throw new ArgumentException("Entities cannot be empty.", nameof(entities));

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

        var batchSqlV = BuildBatchUpsertSql_VersionedReturning(
            QualifiedTableName(),
            columns,
            primaryKeyColumns,
            list.Count);

        var batchParams = new List<object?>(capacity: list.Count * fields.Length);
        foreach (var e in list)
            batchParams.AddRange(GetParameterValues(e, fields, includeTimestamp: false));

        List<UpsertReturnRow> returnedRows;
        try
        {
            returnedRows = (await _databaseProvider.QueryAsync<UpsertReturnRow>(
                batchSqlV,
                batchParams,
                ct).ConfigureAwait(false)).ToList();
        }
        catch (Exception ex)
        {
            throw new OptimisticConcurrencyException(
                typeof(TVaultModel),
                storageId: null,
                message: $"Optimistic concurrency failure saving batch of {typeof(TVaultModel).Name}. One or more rows had a version mismatch.",
                inner: ex,
                expectedAffected: list.Count,
                actualAffected: null);
        }

        if (returnedRows.Count != list.Count)
        {
            throw new OptimisticConcurrencyException(
                typeof(TVaultModel),
                storageId: null,
                message: $"Optimistic concurrency failure saving batch of {typeof(TVaultModel).Name}. Expected {list.Count} rows, got {returnedRows.Count}.",
                expectedAffected: list.Count,
                actualAffected: returnedRows.Count);
        }

        var byId = returnedRows
            .Where(r => !string.IsNullOrWhiteSpace(r.StorageId))
            .ToDictionary(r => r.StorageId, r => r.Version, StringComparer.Ordinal);

        foreach (var e in list)
        {
            if (!string.IsNullOrWhiteSpace(e.StorageId) && byId.TryGetValue(e.StorageId, out var v))
                SetVersionValue(e, v);
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

    protected virtual string BuildHistoryQuery(IReadOnlyList<string> columns)
    {
        var histQualified = $"{QuoteIdent(Keyspace.Name)}.{QuoteIdent(VaultDocument.Name + "_history")}";

        var cols = string.Join(", ", columns.Select(QuoteIdent));
        var vals = string.Join(", ", columns.Select(_ => "?"));
        return $"INSERT INTO {histQualified} ({cols}, {QuoteIdent("timestamp")}) VALUES ({vals}, ?)";
    }

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
        // EF Core-style update is a chain of method calls:
        // calls = calls.SetProperty(e => e.Prop, e => <value>)
        // Walk method-call chain collecting (propSelector, valueSelector).
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
        // Compile value selectors per assignment (assign lists are usually small).
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
