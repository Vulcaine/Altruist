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

namespace Altruist.Persistence;

public enum QueryPosition
{
    SELECT,
    FROM,
    WHERE,
    ORDER_BY,
    LIMIT,
    UPDATE,
    SET
}

/// <summary>
/// Immutable per-chain query state.
/// Each fluent call creates a new state with one extra piece added.
/// </summary>
internal sealed class QueryState
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
            { QueryPosition.UPDATE,   new List<object?>() },
            { QueryPosition.SET,      new List<object?>() }
        };
    }

    private QueryState(Dictionary<QueryPosition, HashSet<string>> parts,
                       Dictionary<QueryPosition, List<object?>> parameters)
    {
        Parts = parts;
        Parameters = parameters;
    }

    public QueryState With(QueryPosition pos, string part, object? parameter = null)
    {
        // clone shallow; copy only the mutated bucket
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

        var projection = string.Join(", ", doc.Columns.Select(kvp => $"{kvp.Value} AS {kvp.Key}"));
        return With(QueryPosition.SELECT, projection);
    }
}

public class CqlVault<TVaultModel> : ICqlVault<TVaultModel> where TVaultModel : class, IVaultModel
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ICqlDatabaseProvider _databaseProvider;

    // immutable per-chain state
    private readonly QueryState _state;

    public IKeyspace Keyspace { get; }
    protected Document _document { get; }

    public IHistoricalVault<TVaultModel> History => throw new NotImplementedException();

    private static readonly VaultMetadata _vaultMeta = VaultRegistry.GetByClr(typeof(TVaultModel));

    private static readonly Action<object, DateTime>? _timestampSetter =
        TimestampSetterFactory.Create(typeof(TVaultModel));

    public CqlVault(ICqlDatabaseProvider databaseProvider, IKeyspace keyspace, Document document, IServiceProvider serviceProvider)
    : this(databaseProvider, keyspace, document, serviceProvider, new QueryState())
    { }

    private CqlVault(ICqlDatabaseProvider databaseProvider, IKeyspace keyspace, Document document, IServiceProvider serviceProvider, QueryState state)
    {
        _databaseProvider = databaseProvider;
        _document = document;
        Keyspace = keyspace;
        _serviceProvider = serviceProvider;
        _state = state;
    }

    // ------------------------ Fluent query ops (return NEW instance) ------------------------

    public IVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
    {
        string whereClause = ConvertWherePredicateToString(predicate);
        var next = _state.With(QueryPosition.WHERE, whereClause)
                         .EnsureProjectionSelected(_document);
        return New(next);
    }

    public IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        string orderByClause = ConvertOrderByToString(keySelector);
        var next = _state.With(QueryPosition.ORDER_BY, orderByClause)
                         .With(QueryPosition.SELECT, orderByClause);
        return New(next);
    }

    public IVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        string orderByDescClause = ConvertOrderByDescendingToString(keySelector);
        var next = _state.With(QueryPosition.ORDER_BY, orderByDescClause + " DESC")
                         .With(QueryPosition.SELECT, orderByDescClause);
        return New(next);
    }

    public IVault<TVaultModel> Take(int count)
    {
        var next = _state.With(QueryPosition.LIMIT, $"LIMIT {count}")
                         .EnsureProjectionSelected(_document);
        return New(next);
    }

    public IVault<TVaultModel> Skip(int count)
        => throw new NotSupportedException("Cassandra does not support skipping rows. Use paging with a WHERE clause instead.");

    // ------------------------ Terminal ops (use current state) ------------------------

    public async Task<List<TVaultModel>> ToListAsync()
    {
        var st = _state.EnsureProjectionSelected(_document);
        string query = BuildSelectQuery(st);
        return (await _databaseProvider.QueryAsync<TVaultModel>(query, st.Parameters[QueryPosition.SELECT])).ToList();
    }

    public async Task<TVaultModel?> FirstOrDefaultAsync()
    {
        var st = _state.EnsureProjectionSelected(_document)
                       .With(QueryPosition.LIMIT, "LIMIT 1");
        string query = BuildSelectQuery(st);
        var result = await _databaseProvider.QueryAsync<TVaultModel>(query, st.Parameters[QueryPosition.SELECT]);
        return result.FirstOrDefault();
    }

    public async Task<TVaultModel?> FirstAsync()
    {
        var st = _state.EnsureProjectionSelected(_document)
                       .With(QueryPosition.LIMIT, "LIMIT 1");
        string query = BuildSelectQuery(st);
        var result = await _databaseProvider.QueryAsync<TVaultModel>(query, st.Parameters[QueryPosition.SELECT]);
        return result.First();
    }

    public async Task<List<TVaultModel>> ToListAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        var next = (CqlVault<TVaultModel>)Where(predicate);
        return await next.ToListAsync();
    }

    public async Task<long> CountAsync()
    {
        var whereClause = string.Join(" AND ", _state.Parts[QueryPosition.WHERE]);
        var countQuery = string.IsNullOrEmpty(whereClause)
            ? $"SELECT COUNT(*) FROM {_document.Name}"
            : $"SELECT COUNT(*) FROM {_document.Name} WHERE {whereClause}";

        return await _databaseProvider.ExecuteCountAsync(countQuery, _state.Parameters[QueryPosition.WHERE]);
    }

    public async Task<long> UpdateAsync(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls)
    {
        var updatedProperties = ExtractSetProperties(setPropertyCalls);
        var setStrings = updatedProperties.Select(kv => (object)$"{kv.Key} = {ConvertToCqlValue(kv.Value)}").ToList();

        // bake SET entries into a local state for this execution
        var st = _state;
        foreach (var s in setStrings)
            st = st.With(QueryPosition.SET, (string)s)!;

        string updateQuery = BuildUpdateQuery(st);
        var concatenated = st.Parameters[QueryPosition.WHERE]
            .Concat(st.Parameters[QueryPosition.SET]).ToList();

        return await _databaseProvider.ExecuteAsync(updateQuery, concatenated);
    }

    public async Task<bool> DeleteAsync()
    {
        string deleteQuery = $"DELETE FROM {_document.Name}";
        var whereClause = string.Join(" AND ", _state.Parts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(whereClause))
            deleteQuery += $" WHERE {whereClause}";

        long affectedRows = await _databaseProvider.ExecuteAsync(deleteQuery, _state.Parameters[QueryPosition.WHERE]);
        return affectedRows > 0;
    }

    public Task<ICursor<TVaultModel>> ToCursorAsync()
        => throw new NotImplementedException();

    public async Task<IEnumerable<TResult>> SelectAsync<TResult>(
        Expression<Func<TVaultModel, TResult>> selector) where TResult : class, IVaultModel
    {
        if (_state.Parts[QueryPosition.SELECT].Contains("*"))
            throw new InvalidOperationException("Invalid query. SELECT * followed by other columns is not allowed.");

        var st = _state;
        foreach (var column in ConvertSelectExpressionToString(selector))
            st = st.With(QueryPosition.SELECT, column);

        var query = BuildSelectQuery(st);
        return await _databaseProvider.QueryAsync<TResult>(query, st.Parameters[QueryPosition.SELECT]);
    }

    public async Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        var next = (CqlVault<TVaultModel>)Where(predicate);
        var where = string.Join(" AND ", next._state.Parts[QueryPosition.WHERE]);
        var query = string.IsNullOrEmpty(where)
            ? $"SELECT COUNT(*) FROM {_document.Name}"
            : $"SELECT COUNT(*) FROM {_document.Name} WHERE {where}";

        var count = await _databaseProvider.ExecuteCountAsync(query, next._state.Parameters[QueryPosition.WHERE]);
        return count > 0;
    }

    // ------------------------ Non-query ops (unchanged) ------------------------

    public async Task SaveAsync(TVaultModel entity, bool? saveHistory = false)
    {
        await SaveEntityAsync(entity, saveHistory);
    }

    public Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false) =>
        SaveEntitiesAsync(entities, saveHistory);

    private CqlVault<TVaultModel> New(QueryState st)
        => new CqlVault<TVaultModel>(_databaseProvider, Keyspace, _document, _serviceProvider, st);

    // ------------------------ Query building helpers ------------------------

    private string BuildSelectQuery(QueryState st)
    {
        var select = st.Parts[QueryPosition.SELECT].Count == 0
            ? string.Join(", ", _document.Columns.Select(kvp => $"{kvp.Value} AS {kvp.Key}"))
            : string.Join(", ", st.Parts[QueryPosition.SELECT]);

        string selectQuery = $"SELECT {select} FROM {_document.Name}";

        var where = string.Join(" AND ", st.Parts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(where))
            selectQuery += $" WHERE {where}";

        var orderBy = string.Join(", ", st.Parts[QueryPosition.ORDER_BY]);
        if (!string.IsNullOrEmpty(orderBy))
            selectQuery += $" ORDER BY {orderBy}";

        var limit = string.Join(" ", st.Parts[QueryPosition.LIMIT]);
        if (!string.IsNullOrEmpty(limit))
            selectQuery += $" {limit}";

        return selectQuery;
    }

    private string BuildUpdateQuery(QueryState st)
    {
        var set = string.Join(", ", st.Parts[QueryPosition.SET]);
        var updateQuery = $"UPDATE {_document.Name} SET {set}";

        var where = string.Join(" AND ", st.Parts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(where))
            updateQuery += $" WHERE {where}";

        return updateQuery;
    }

    private IEnumerable<string> ConvertSelectExpressionToString<TResult>(Expression<Func<TVaultModel, TResult>> selector)
    {
        if (selector.Body is NewExpression ne && ne.Members is not null)
        {
            foreach (var m in ne.Members)
            {
                var propName = m.Name;
                var column = _document.Columns.TryGetValue(propName, out var c) ? c : Document.ToCamelCase(propName);
                yield return $"{column} AS {propName}";
            }
            yield break;
        }

        throw new NotSupportedException("Unsupported expression type for SELECT. Use: x => new(...) with properties.");
    }

    private string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate)
    {
        var modelParam = predicate.Parameters[0];
        var sql = ToWhere(predicate.Body, modelParam);
        return sql;
    }

    private string ToWhere(Expression expr, ParameterExpression modelParam)
    {
        switch (expr)
        {
            case UnaryExpression ue when ue.NodeType is ExpressionType.Convert or ExpressionType.ConvertChecked:
                return ToWhere(ue.Operand, modelParam);

            case BinaryExpression be when be.NodeType is ExpressionType.AndAlso or ExpressionType.OrElse:
                {
                    var op = GetOperator(be.NodeType);
                    var left = ToWhere(be.Left, modelParam);
                    var right = ToWhere(be.Right, modelParam);
                    return $"({left}) {op} ({right})";
                }

            case BinaryExpression be when IsComparison(be.NodeType):
                {
                    // Allow col == value and value == col (with flipped op for <, >, <=, >=)
                    if (!TryReadColumnName(be.Left, modelParam, out var col))
                    {
                        if (TryReadColumnName(be.Right, modelParam, out var colSwapped))
                        {
                            var cmp = FlipOperator(be.NodeType);
                            var val = ExpressionUtils.Evaluate(be.Left);
                            return $"{colSwapped} {GetOperator(cmp)} {ConvertToCqlValue(val)}";
                        }
                        throw new NotSupportedException("WHERE comparison must involve a model property.");
                    }

                    var valueObj = ExpressionUtils.Evaluate(be.Right);
                    return $"{col} {GetOperator(be.NodeType)} {ConvertToCqlValue(valueObj)}";
                }

            default:
                throw new NotSupportedException("Unsupported expression in WHERE.");
        }
    }

    private static bool IsComparison(ExpressionType t) =>
        t is ExpressionType.Equal or ExpressionType.NotEqual
          or ExpressionType.GreaterThan or ExpressionType.GreaterThanOrEqual
          or ExpressionType.LessThan or ExpressionType.LessThanOrEqual;

    private static ExpressionType FlipOperator(ExpressionType t) => t switch
    {
        ExpressionType.GreaterThan => ExpressionType.LessThan,
        ExpressionType.GreaterThanOrEqual => ExpressionType.LessThanOrEqual,
        ExpressionType.LessThan => ExpressionType.GreaterThan,
        ExpressionType.LessThanOrEqual => ExpressionType.GreaterThanOrEqual,
        _ => t // Equal / NotEqual are symmetric
    };

    private bool TryReadColumnName(Expression expression, ParameterExpression modelParam, out string column)
    {
        // Peel conversions first
        while (expression is UnaryExpression ue &&
               (ue.NodeType == ExpressionType.Convert || ue.NodeType == ExpressionType.ConvertChecked))
            expression = ue.Operand;

        if (expression is MemberExpression me && IsRootedInParameter(me, modelParam))
        {
            var propName = me.Member.Name;
            if (_document.Columns.TryGetValue(propName, out var col))
            {
                column = col;
                return true;
            }

            column = Document.ToCamelCase(propName);
            return true;
        }

        column = "";
        return false;
    }

    private static bool IsRootedInParameter(MemberExpression me, ParameterExpression modelParam)
    {
        Expression? root = me.Expression;
        while (root is MemberExpression inner)
            root = inner.Expression;

        return root == modelParam;
    }

    private static string GetOperator(ExpressionType expressionType) =>
        expressionType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.NotEqual => "!=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.GreaterThanOrEqual => ">=",
            ExpressionType.LessThan => "<",
            ExpressionType.LessThanOrEqual => "<=",
            ExpressionType.AndAlso => "AND",
            ExpressionType.OrElse => "OR",
            _ => throw new NotSupportedException($"Unsupported operator: {expressionType}")
        };

    private string ConvertOrderByToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        if (keySelector.Body is MemberExpression me)
            return _document.Columns.TryGetValue(me.Member.Name, out var col) ? col : me.Member.Name;

        throw new NotSupportedException("Unsupported expression type in ORDER BY clause.");
    }

    private string ConvertOrderByDescendingToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        if (keySelector.Body is MemberExpression me)
            return _document.Columns.TryGetValue(me.Member.Name, out var col) ? col : me.Member.Name;

        throw new NotSupportedException("Unsupported expression type in ORDER BY DESC clause.");
    }

    private Dictionary<string, object> ExtractSetProperties(
        Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls)
    {
        var updated = new Dictionary<string, object>(StringComparer.Ordinal);

        if (setPropertyCalls.Body is MemberInitExpression mi)
        {
            foreach (var binding in mi.Bindings.OfType<MemberAssignment>())
            {
                var propName = binding.Member.Name;
                var value = Expression.Lambda(binding.Expression).Compile().DynamicInvoke()!;
                var column = _document.Columns.TryGetValue(propName, out var c) ? c : Document.ToCamelCase(propName);
                updated[column] = value;
            }
        }

        return updated;
    }

    // ------------------------ Insert/History/save helpers ------------------------

    private async Task SaveEntityAsync(TVaultModel entity, bool? saveHistory = false)
    {
        if (entity is null)
            throw new ArgumentNullException(nameof(entity));
        entity.OnSave();

        var tableName = _document.Name;
        var fields = _document.Columns.Keys;
        var insertQuery = BuildInsertQuery(tableName, _document.Columns.Values);

        var parameters = GetParameterValues(entity, fields, includeTimestamp: false).ToList();
        var queries = new List<string> { insertQuery };

        if (_document.StoreHistory == true)
        {
            if (saveHistory == true)
            {
                var historyQuery = BuildHistoryQuery(tableName, _document.Columns.Values);
                queries.Add(historyQuery);

                var historyParams = GetParameterValues(entity, fields, includeTimestamp: true);
                parameters.AddRange(historyParams.Skip(fields.Count()));
            }
        }
        else if (saveHistory == true)
        {
            throw new InvalidOperationException($"History is not enabled for the table {tableName}. Consider enabling StoreHistory=true.");
        }

        var batchQuery = $"BEGIN BATCH {string.Join(" ", queries)} APPLY BATCH;";

        var canSave = true;
        if (entity is IBeforeVaultSave before)
            canSave = await before.BeforeSaveAsync(_serviceProvider);

        if (canSave)
            await _databaseProvider.ExecuteAsync(batchQuery, parameters!);

        if (entity is IAfterVaultSave after)
            await after.AfterSaveAsync(_serviceProvider);
    }

    private async Task SaveEntitiesAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false)
    {
        if (entities is null)
            throw new ArgumentNullException(nameof(entities));
        var list = entities as IList<TVaultModel> ?? entities.ToList();
        if (list.Count == 0)
            throw new ArgumentException("Entities cannot be empty.", nameof(entities));

        foreach (var entity in list)
        {
            entity.OnSave();
        }

        var tableName = _document.Name;
        var fields = _document.Columns.Keys;
        var insertQuery = BuildInsertQuery(tableName, _document.Columns.Values);
        var historyQuery = BuildHistoryQuery(tableName, _document.Columns.Values);

        var queries = new List<string>();
        var allParams = new List<object?>();

        foreach (var e in list)
        {
            var canSave = true;
            if (e is IBeforeVaultSave b)
                canSave = await b.BeforeSaveAsync(_serviceProvider);
            if (!canSave)
                continue;

            queries.Add(insertQuery);
            allParams.AddRange(GetParameterValues(e, fields, includeTimestamp: false));

            if (_document.StoreHistory == true)
            {
                if (saveHistory == true)
                {
                    queries.Add(historyQuery);
                    allParams.AddRange(GetParameterValues(e, fields, includeTimestamp: true).Skip(fields.Count()));
                }
            }
            else if (saveHistory == true)
            {
                throw new InvalidOperationException($"History is not enabled for the table {tableName}. Consider enabling StoreHistory=true.");
            }
        }

        if (queries.Count == 0)
            return;

        var batchQuery = $"BEGIN BATCH {string.Join(" ", queries)} APPLY BATCH;";
        await _databaseProvider.ExecuteAsync(batchQuery, allParams!);

        foreach (var e in list)
            if (e is IAfterVaultSave a)
                await a.AfterSaveAsync(_serviceProvider);
    }

    private static string Q(string ident) => $"\"{ident.Replace("\"", "\"\"")}\"";

    private string BuildInsertQuery(string tableName, IEnumerable<string> columns)
    {
        // tableName is logical (e.g., "account"). Quote it.
        var cols = columns.Select(Q);
        var values = string.Join(", ", columns.Select(_ => "?"));
        return $"INSERT INTO {Q(tableName)} ({string.Join(", ", cols)}) VALUES ({values})";
    }

    private string BuildHistoryQuery(string tableName, IEnumerable<string> columns)
    {
        var cols = columns.Select(Q);
        var values = string.Join(", ", columns.Select(_ => "?"));
        return $"INSERT INTO {Q(tableName + "_history")} ({string.Join(", ", cols)}, {Q("timestamp")}) VALUES ({values}, ?)";
    }

    private object?[] GetParameterValues(TVaultModel entity, IEnumerable<string> fields, bool includeTimestamp)
    {
        var values = fields.Select(field => _document.PropertyAccessors[field](entity)).ToList();

        if (includeTimestamp && _timestampSetter is not null)
        {
            var now = DateTime.UtcNow;
            _timestampSetter(entity, now);
            values.Add(now);
        }

        return values.ToArray();
    }

    private static string ConvertTimeSpanToDatabaseFormat(TimeSpan ts)
    {
        if (ts.TotalSeconds < 60)
            return $"{ts.Seconds}s";
        if (ts.TotalMinutes < 60)
            return $"{ts.Minutes}m";
        if (ts.TotalHours < 24)
            return $"{ts.Hours}h";
        return $"{ts.Days}d";
    }

    private static string ConvertToCqlValue(object? value) =>
        value switch
        {
            null => "NULL",
            string s => $"'{s.Replace("'", "''")}'",
            bool b => b ? "TRUE" : "FALSE",
            DateTime dt => $"'{dt:yyyy-MM-dd HH:mm:ss}'",
            TimeSpan ts => $"'{ConvertTimeSpanToDatabaseFormat(ts)}'",
            Enum e => $"'{e}'",
            _ => value.ToString()!
        };
}

internal static class TimestampSetterFactory
{
    private static readonly ConcurrentDictionary<Type, Action<object, DateTime>?> _cache = new();

    public static Action<object, DateTime>? Create(Type modelType)
    {
        return _cache.GetOrAdd(modelType, static t =>
        {
            var prop = t.GetProperty("Timestamp", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
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

public static class ExpressionUtils
{
    public static object? Evaluate(Expression e)
    {
        if (e.NodeType == ExpressionType.Constant)
            return ((ConstantExpression)e).Value;
        return Expression.Lambda(e).Compile().DynamicInvoke();
    }
}
