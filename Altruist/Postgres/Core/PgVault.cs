// PgVault.cs
/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Linq.Expressions;

using Microsoft.EntityFrameworkCore.Query;

namespace Altruist.Persistence.Postgres;


/// <summary>
/// PostgreSQL vault with a fluent API similar to the CQL vault.
/// </summary>
public class PgVault<TVaultModel> : IVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    protected readonly ISqlDatabaseProvider _databaseProvider;
    protected readonly QueryState _state;

    public IKeyspace Keyspace { get; }
    public readonly Document VaultDocument;

    internal ISqlDatabaseProvider DatabaseProvider => _databaseProvider;

    private readonly IHistoricalVault<TVaultModel> _history;

    public IHistoricalVault<TVaultModel> History
    {
        get
        {
            if (!VaultDocument.StoreHistory)
                throw new InvalidOperationException("History is not enabled for this document.");

            return _history;
        }
    }

    public PgVault(
        ISqlDatabaseProvider databaseProvider,
        IKeyspace schema,
        Document document)
        : this(databaseProvider, schema, document, new QueryState())
    {
    }

    protected PgVault(
        ISqlDatabaseProvider databaseProvider,
        IKeyspace schema,
        Document document,
        QueryState state)
    {
        _databaseProvider = databaseProvider;
        VaultDocument = document;
        Keyspace = schema;
        _state = state;

        _history = new PgHistoricalVault<TVaultModel>(this);
    }

    protected virtual PgVault<TVaultModel> Create(QueryState state)
        => new PgVault<TVaultModel>(_databaseProvider, Keyspace, VaultDocument, state);

    protected PgVault<TVaultModel> New(QueryState state) => Create(state);

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

        var next = (PgVault<TVaultModel>)Where(predicate);
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
        foreach (var column in PgQueryTranslator.Select(selector, VaultDocument))
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

        var next = (PgVault<TVaultModel>)Where(predicate);
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

    public virtual async Task<long> UpdateAsync(
        Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sql = PgQueryTranslator.BuildUpdate(
            setPropertyCalls,
            _state,
            VaultDocument,
            QualifiedTableName());

        var affected = await _databaseProvider.ExecuteAsync(
            sql,
            parameters: null,
            ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
        return affected;
    }

    public virtual async Task UpdateAsync(
        IReadOnlyDictionary<string, object?> primaryKey,
        IReadOnlyDictionary<string, object?> changes,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var sql = PgQueryTranslator.BuildUpdate(
            primaryKey,
            changes,
            VaultDocument,
            QualifiedTableName());

        await _databaseProvider.ExecuteAsync(
            sql,
            parameters: null,
            ct).ConfigureAwait(false);

        ct.ThrowIfCancellationRequested();
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

    // Cursor
    public virtual Task<ICursor<TVaultModel>> ToCursorAsync(CancellationToken ct = default)
        => throw new NotImplementedException();

    // ------------------------ Save ------------------------

    public virtual Task SaveAsync(TVaultModel entity, bool? saveHistory = false, CancellationToken ct = default)
        => SaveEntityAsync(entity, saveHistory, ct);

    public virtual Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false, CancellationToken ct = default)
        => SaveEntitiesAsync(entities, saveHistory, ct);

    // ------------------------ Query building helpers ------------------------

    public virtual string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate)
        => PgQueryTranslator.Where(predicate, VaultDocument);

    internal virtual string ConvertOrderByToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
        => PgQueryTranslator.OrderBy(keySelector, VaultDocument);

    internal virtual string ConvertOrderByDescendingToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
        => PgQueryTranslator.OrderBy(keySelector, VaultDocument);

    private string BuildSelectQuery(QueryState st)
    {
        var select =
            st.Parts[QueryPosition.SELECT].Count == 0
                ? string.Join(", ",
                    VaultDocument.Columns.Select(kvp => $"{QuoteIdent(kvp.Value)} AS {QuoteIdent(kvp.Key)}"))
                : string.Join(", ", st.Parts[QueryPosition.SELECT]);

        var sql = $"SELECT {select} FROM {QualifiedTableName()}";

        var where = string.Join(" AND ", st.Parts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(where))
            sql += $" WHERE {where}";

        var orderBy = string.Join(", ", st.Parts[QueryPosition.ORDER_BY]);
        if (!string.IsNullOrEmpty(orderBy))
            sql += $" ORDER BY {orderBy}";

        var limit = string.Join(" ", st.Parts[QueryPosition.LIMIT]);
        if (!string.IsNullOrEmpty(limit))
            sql += $" {limit}";

        var offset = string.Join(" ", st.Parts[QueryPosition.OFFSET]);
        if (!string.IsNullOrEmpty(offset))
            sql += $" {offset}";

        return sql;
    }

    private static string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";

    private string QualifiedTableName()
        => $"{QuoteIdent(Keyspace.Name)}.{QuoteIdent(VaultDocument.Name)}";

    // ------------------------ Save helpers ------------------------

    private async Task SaveEntityAsync(TVaultModel entity, bool? saveHistory, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (entity is null)
            throw new ArgumentNullException(nameof(entity));

        entity.OnSave();

        var fields = VaultDocument.Fields.ToArray();
        var columns = fields.Select(f => VaultDocument.Columns[f]).ToArray();

        var pkKeys = VaultDocument.PrimaryKey?.Keys ?? Array.Empty<string>();
        var primaryKeyColumns = pkKeys
            .Select(k => VaultDocument.Columns.TryGetValue(k, out var col) ? col : k)
            .ToArray();

        var upsertQuery = BuildUpsertQuery(
            QualifiedTableName(),
            columns,
            primaryKeyColumns);

        var parameters = GetParameterValues(entity, fields, includeTimestamp: false).ToList();
        var queries = new List<string> { upsertQuery };

        if (VaultDocument.StoreHistory == true)
        {
            if (saveHistory == true)
            {
                var historyQuery = BuildHistoryQuery(VaultDocument.Name, columns);
                queries.Add(historyQuery);

                var historyParams = GetParameterValues(entity, fields, includeTimestamp: true);
                parameters.AddRange(historyParams.Skip(fields.Length));
            }
        }
        else if (saveHistory == true)
        {
            throw new InvalidOperationException(
                $"History is not enabled for the table {VaultDocument.Name}. Consider enabling StoreHistory=true.");
        }

        var batchQuery = string.Join(";", queries) + ";";

        await _databaseProvider.ExecuteAsync(
            batchQuery,
            parameters,
            ct).ConfigureAwait(false);
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
        }

        var fields = VaultDocument.Fields.ToArray();
        var columns = fields.Select(f => VaultDocument.Columns[f]).ToArray();

        var pkKeys = VaultDocument.PrimaryKey?.Keys ?? Array.Empty<string>();
        var primaryKeyColumns = pkKeys
            .Select(k => VaultDocument.Columns.TryGetValue(k, out var col) ? col : k)
            .ToArray();

        var upsertQuery = BuildUpsertQuery(
            QualifiedTableName(),
            columns,
            primaryKeyColumns);

        string? historyQuery = null;
        if (VaultDocument.StoreHistory == true)
        {
            if (saveHistory == true)
                historyQuery = BuildHistoryQuery(VaultDocument.Name, columns);
        }
        else if (saveHistory == true)
        {
            throw new InvalidOperationException(
                $"History is not enabled for the table {VaultDocument.Name}. Consider enabling StoreHistory=true.");
        }

        var queries = new List<string>();
        var allParams = new List<object?>();

        foreach (var e in list)
        {
            ct.ThrowIfCancellationRequested();

            queries.Add(upsertQuery);
            allParams.AddRange(GetParameterValues(e, fields, includeTimestamp: false));

            if (historyQuery is not null)
            {
                queries.Add(historyQuery);
                var historyParams = GetParameterValues(e, fields, includeTimestamp: true);
                allParams.AddRange(historyParams.Skip(fields.Length));
            }
        }

        if (queries.Count == 0)
            return;

        var batchQuery = string.Join(";", queries) + ";";

        await _databaseProvider.ExecuteAsync(
            batchQuery,
            allParams,
            ct).ConfigureAwait(false);
    }

    private static string BuildUpsertQuery(
        string tableName,
        IReadOnlyList<string> columns,
        IReadOnlyList<string> primaryKeyNames)
    {
        if (primaryKeyNames is null || primaryKeyNames.Count == 0)
            throw new ArgumentException("At least one primary key column must be specified.", nameof(primaryKeyNames));

        var columnNames = columns.Select(c => $"\"{c}\"").ToArray();
        var placeholders = columns.Select(_ => "?").ToArray();

        var insert =
            $"INSERT INTO {tableName} ({string.Join(", ", columnNames)}) " +
            $"VALUES ({string.Join(", ", placeholders)})";

        var pkList = string.Join(", ", primaryKeyNames.Select(pk => $"\"{pk}\""));
        var pkSet = new HashSet<string>(primaryKeyNames, StringComparer.OrdinalIgnoreCase);

        var setClauses = columns
            .Where(c => !pkSet.Contains(c))
            .Select(c => $"\"{c}\" = EXCLUDED.\"{c}\"");

        return $"{insert} ON CONFLICT ({pkList}) DO UPDATE SET {string.Join(", ", setClauses)}";
    }

    private string BuildHistoryQuery(string tableNameIgnored, IEnumerable<string> columns)
    {
        var histQualified =
            $"{QuoteIdent(Keyspace.Name)}.{QuoteIdent(VaultDocument.Name + "_history")}";

        var cols = string.Join(", ", columns.Select(QuoteIdent));
        var vals = string.Join(", ", columns.Select(_ => "?"));
        return $"INSERT INTO {histQualified} ({cols}, {QuoteIdent("timestamp")}) VALUES ({vals}, ?)";
    }

    private object?[] GetParameterValues(TVaultModel entity, IEnumerable<string> fields, bool includeTimestamp)
    {
        var values = fields.Select(field => VaultDocument.PropertyAccessors[field](entity)).ToList();

        if (includeTimestamp)
        {
            var now = DateTime.UtcNow;
            values.Add(now);
        }

        return values.ToArray();
    }
}
