/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;

using Microsoft.EntityFrameworkCore.Query;

namespace Altruist.Persistence.Postgres;

public enum QueryPosition
{
    SELECT,
    FROM,
    WHERE,
    ORDER_BY,
    LIMIT,
    OFFSET,
    UPDATE,
    SET
}

/// <summary>
/// Immutable per-chain query state.
/// Each fluent call creates a new state with one extra piece added.
/// </summary>
public sealed class QueryState
{
    public readonly Dictionary<QueryPosition, HashSet<string>> Parts;
    public readonly Dictionary<QueryPosition, List<object?>> Parameters;

    public QueryState()
    {
        Parts = new Dictionary<QueryPosition, HashSet<string>>
        {
            { QueryPosition.SELECT,   new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.FROM,     new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.WHERE,    new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.ORDER_BY, new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.LIMIT,    new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.OFFSET,   new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.UPDATE,   new HashSet<string>(StringComparer.Ordinal) },
            { QueryPosition.SET,      new HashSet<string>(StringComparer.Ordinal) }
        };

        Parameters = new Dictionary<QueryPosition, List<object?>>
        {
            { QueryPosition.SELECT,   new List<object?>() },
            { QueryPosition.FROM,     new List<object?>() },
            { QueryPosition.WHERE,    new List<object?>() },
            { QueryPosition.ORDER_BY, new List<object?>() },
            { QueryPosition.LIMIT,    new List<object?>() },
            { QueryPosition.OFFSET,   new List<object?>() },
            { QueryPosition.UPDATE,   new List<object?>() },
            { QueryPosition.SET,      new List<object?>() }
        };
    }

    private QueryState(
        Dictionary<QueryPosition, HashSet<string>> parts,
        Dictionary<QueryPosition, List<object?>> parameters)
    {
        Parts = parts;
        Parameters = parameters;
    }

    public QueryState With(QueryPosition pos, string part, object? parameter = null)
    {
        var newParts = new Dictionary<QueryPosition, HashSet<string>>(Parts.Count);
        foreach (var kv in Parts)
        {
            if (kv.Key == pos)
            {
                var copy = new HashSet<string>(kv.Value, StringComparer.Ordinal);
                copy.Add(part);
                newParts[kv.Key] = copy;
            }
            else
            {
                newParts[kv.Key] = kv.Value;
            }
        }

        var newParams = new Dictionary<QueryPosition, List<object?>>(Parameters.Count);
        foreach (var kv in Parameters)
        {
            if (kv.Key == pos && parameter is not null)
            {
                var copy = new List<object?>(kv.Value);
                copy.Add(parameter);
                newParams[kv.Key] = copy;
            }
            else
            {
                newParams[kv.Key] = kv.Value;
            }
        }

        return new QueryState(newParts, newParams);
    }

    public bool HasAny(QueryPosition pos) => Parts[pos].Count > 0;

    public QueryState EnsureProjectionSelected(Document doc)
    {
        if (HasAny(QueryPosition.SELECT))
            return this;

        var projection = string.Join(", ",
            doc.Columns.Select(kvp => $"{QuoteIdent(kvp.Value)} AS {QuoteIdent(kvp.Key)}"));

        return With(QueryPosition.SELECT, projection);
    }

    private static string QuoteIdent(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";
}

/// <summary>
/// PostgreSQL vault with a fluent API similar to the CQL vault.
/// </summary>
public class PgVault<TVaultModel> : IVault<TVaultModel>
    where TVaultModel : class, IVaultModel
{
    protected readonly IServiceProvider _serviceProvider;
    protected readonly ISqlDatabaseProvider _databaseProvider;
    protected readonly QueryState _state;

    protected readonly IServiceProvider Services;

    public IKeyspace Keyspace { get; }
    public readonly Document VaultDocument;

    internal ISqlDatabaseProvider DatabaseProvider => _databaseProvider;

    private IHistoricalVault<TVaultModel> _history;

    public IHistoricalVault<TVaultModel> History
    {
        get
        {
            if (!VaultDocument.StoreHistory)
                throw new InvalidOperationException("History is not enabled for this document.");

            return _history;
        }
    }

    private static readonly Action<object, DateTime>? _timestampSetter =
        TimestampSetterFactory.Create(typeof(TVaultModel));

    public PgVault(
        ISqlDatabaseProvider databaseProvider,
        IKeyspace schema,
        Document document,
        IServiceProvider services)
        : this(databaseProvider, schema, document, services, new QueryState())
    {
    }

    protected PgVault(
        ISqlDatabaseProvider databaseProvider,
        IKeyspace schema,
        Document document,
        IServiceProvider services,
        QueryState state)
    {
        _databaseProvider = databaseProvider;
        _serviceProvider = services;
        Services = services;

        VaultDocument = document;
        Keyspace = schema;
        _state = state;

        _history = new PgHistoricalVault<TVaultModel>(this);
    }

    protected virtual PgVault<TVaultModel> Create(QueryState state)
        => new PgVault<TVaultModel>(_databaseProvider, Keyspace, VaultDocument, Services, state);

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

    public virtual async Task<List<TVaultModel>> ToListAsync()
    {
        var st = _state.EnsureProjectionSelected(VaultDocument);
        var query = BuildSelectQuery(st);

        var parameters =
            st.Parameters[QueryPosition.WHERE]
                .Concat(st.Parameters[QueryPosition.SELECT])
                .Concat(st.Parameters[QueryPosition.ORDER_BY])
                .Concat(st.Parameters[QueryPosition.LIMIT])
                .Concat(st.Parameters[QueryPosition.OFFSET])
                .ToList();

        return (await _databaseProvider.QueryAsync<TVaultModel>(query, parameters!)).ToList();
    }

    public virtual async Task<TVaultModel?> FirstOrDefaultAsync()
    {
        var st = _state.EnsureProjectionSelected(VaultDocument)
                       .With(QueryPosition.LIMIT, "LIMIT 1");

        var query = BuildSelectQuery(st);

        var parameters =
            st.Parameters[QueryPosition.WHERE]
                .Concat(st.Parameters[QueryPosition.SELECT])
                .Concat(st.Parameters[QueryPosition.ORDER_BY])
                .Concat(st.Parameters[QueryPosition.LIMIT])
                .Concat(st.Parameters[QueryPosition.OFFSET])
                .ToList();

        var result = await _databaseProvider.QueryAsync<TVaultModel>(query, parameters!);
        return result.FirstOrDefault();
    }

    public virtual async Task<TVaultModel?> FirstAsync()
    {
        var st = _state.EnsureProjectionSelected(VaultDocument)
                       .With(QueryPosition.LIMIT, "LIMIT 1");

        var query = BuildSelectQuery(st);

        var parameters =
            st.Parameters[QueryPosition.WHERE]
                .Concat(st.Parameters[QueryPosition.SELECT])
                .Concat(st.Parameters[QueryPosition.ORDER_BY])
                .Concat(st.Parameters[QueryPosition.LIMIT])
                .Concat(st.Parameters[QueryPosition.OFFSET])
                .ToList();

        var result = await _databaseProvider.QueryAsync<TVaultModel>(query, parameters!);
        return result.First();
    }

    public virtual async Task<List<TVaultModel>> ToListAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        var next = (PgVault<TVaultModel>)Where(predicate);
        return await next.ToListAsync();
    }

    public virtual async Task<long> CountAsync()
    {
        var whereClause = string.Join(" AND ", _state.Parts[QueryPosition.WHERE]);
        var from = QualifiedTableName();

        var sql = string.IsNullOrEmpty(whereClause)
            ? $"SELECT COUNT(*) FROM {from}"
            : $"SELECT COUNT(*) FROM {from} WHERE {whereClause}";

        return await _databaseProvider.ExecuteCountAsync(sql, _state.Parameters[QueryPosition.WHERE]!);
    }

    public virtual async Task<long> UpdateAsync(
        Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls)
    {
        var sql = PgQueryTranslator.BuildUpdate(
            setPropertyCalls,
            _state,
            VaultDocument,
            QualifiedTableName());

        return await _databaseProvider.ExecuteAsync(sql, parameters: null);
    }

    public virtual async Task UpdateAsync(
        IReadOnlyDictionary<string, object?> primaryKey,
        IReadOnlyDictionary<string, object?> changes)
    {
        var sql = PgQueryTranslator.BuildUpdate(
            primaryKey,
            changes,
            VaultDocument,
            QualifiedTableName());

        await _databaseProvider.ExecuteAsync(sql, parameters: null);
    }

    public virtual async Task<bool> DeleteAsync()
    {
        var from = QualifiedTableName();
        var whereClause = string.Join(" AND ", _state.Parts[QueryPosition.WHERE]);

        var sql = string.IsNullOrEmpty(whereClause)
            ? $"DELETE FROM {from}"
            : $"DELETE FROM {from} WHERE {whereClause}";

        var affected = await _databaseProvider.ExecuteAsync(sql, _state.Parameters[QueryPosition.WHERE]!);
        return affected > 0;
    }

    public virtual Task<ICursor<TVaultModel>> ToCursorAsync()
        => throw new NotImplementedException();

    public virtual async Task<IEnumerable<TResult>> SelectAsync<TResult>(
        Expression<Func<TVaultModel, TResult>> selector)
        where TResult : class, IVaultModel
    {
        if (_state.Parts[QueryPosition.SELECT].Contains("*"))
            throw new InvalidOperationException("Invalid query. SELECT * followed by other columns is not allowed.");

        var st = _state;
        foreach (var column in PgQueryTranslator.Select(selector, VaultDocument))
            st = st.With(QueryPosition.SELECT, column);

        var query = BuildSelectQuery(st);

        var parameters =
            st.Parameters[QueryPosition.WHERE]
                .Concat(st.Parameters[QueryPosition.SELECT])
                .Concat(st.Parameters[QueryPosition.ORDER_BY])
                .Concat(st.Parameters[QueryPosition.LIMIT])
                .Concat(st.Parameters[QueryPosition.OFFSET])
                .ToList();

        return await _databaseProvider.QueryAsync<TResult>(query, parameters!);
    }

    public virtual async Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        var next = (PgVault<TVaultModel>)Where(predicate);
        var where = string.Join(" AND ", next._state.Parts[QueryPosition.WHERE]);
        var from = next.QualifiedTableName();

        var query = string.IsNullOrEmpty(where)
            ? $"SELECT COUNT(*) FROM {from}"
            : $"SELECT COUNT(*) FROM {from} WHERE {where}";

        var count = await _databaseProvider.ExecuteCountAsync(
            query,
            next._state.Parameters[QueryPosition.WHERE]!);

        return count > 0;
    }

    // ------------------------ Non-query ops ------------------------

    public virtual async Task SaveAsync(TVaultModel entity, bool? saveHistory = false)
    {
        await SaveEntityAsync(entity, saveHistory);
    }

    public virtual Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false) =>
        SaveEntitiesAsync(entities, saveHistory);

    // ------------------------ Translation hooks (override-able) ------------------------

    public virtual string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate)
        => PgQueryTranslator.Where(predicate, VaultDocument);

    internal virtual string ConvertOrderByToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
        => PgQueryTranslator.OrderBy(keySelector, VaultDocument);

    internal virtual string ConvertOrderByDescendingToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
        => PgQueryTranslator.OrderBy(keySelector, VaultDocument);

    // ------------------------ Query building helpers ------------------------

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

    // ------------------------ Saving (RESTORED) ------------------------

    private async Task SaveEntityAsync(TVaultModel entity, bool? saveHistory = false)
    {
        if (entity is null)
            throw new ArgumentNullException(nameof(entity));

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
        await _databaseProvider.ExecuteAsync(batchQuery, parameters!);
    }

    private async Task SaveEntitiesAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false)
    {
        if (entities is null)
            throw new ArgumentNullException(nameof(entities));

        var list = entities as IList<TVaultModel> ?? entities.ToList();
        if (list.Count == 0)
            throw new ArgumentException("Entities cannot be empty.", nameof(entities));

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
            var canSave = true;
            if (!canSave)
                continue;

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
        await _databaseProvider.ExecuteAsync(batchQuery, allParams!);
    }

    /// <summary>
    /// Builds an INSERT ... ON CONFLICT (pk1, pk2, ...) DO UPDATE SET ... upsert statement.
    /// Uses '?' placeholders so the provider can expand them to unique @p1..@pn across batched statements.
    /// IMPORTANT: tableName must already be fully-qualified and quoted.
    /// </summary>
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

        if (includeTimestamp && _timestampSetter is not null)
        {
            var now = DateTime.UtcNow;
            _timestampSetter(entity, now);
            values.Add(now);
        }

        return values.ToArray();
    }
}

internal static class TimestampSetterFactory
{
    private static readonly ConcurrentDictionary<Type, Action<object, DateTime>?> _cache = new();

    public static Action<object, DateTime>? Create(Type modelType)
    {
        return _cache.GetOrAdd(modelType, static t =>
        {
            var prop = t.GetProperty("Timestamp",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            if (prop is null || !prop.CanWrite || prop.PropertyType != typeof(DateTime))
                return null;

            var target = Expression.Parameter(typeof(object), "target");
            var value = Expression.Parameter(typeof(DateTime), "value");

            var casted = Expression.Convert(target, t);
            var member = Expression.Property(casted, prop);
            var assign = Expression.Assign(member, value);

            var lambda = Expression.Lambda<Action<object, DateTime>>(assign, target, value);
            return lambda.Compile();
        });
    }
}