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

public class CqlVault<TVaultModel> : ICqlVault<TVaultModel> where TVaultModel : class, IVaultModel
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ICqlDatabaseProvider _databaseProvider;

    private readonly Dictionary<QueryPosition, HashSet<string>> _queryParts = new()
    {
        { QueryPosition.SELECT,   new HashSet<string>(StringComparer.Ordinal) },
        { QueryPosition.FROM,     new HashSet<string>(StringComparer.Ordinal) },
        { QueryPosition.WHERE,    new HashSet<string>(StringComparer.Ordinal) },
        { QueryPosition.ORDER_BY, new HashSet<string>(StringComparer.Ordinal) },
        { QueryPosition.LIMIT,    new HashSet<string>(StringComparer.Ordinal) },
        { QueryPosition.UPDATE,   new HashSet<string>(StringComparer.Ordinal) },
        { QueryPosition.SET,      new HashSet<string>(StringComparer.Ordinal) }
    };

    private readonly Dictionary<QueryPosition, List<object?>> _queryParameters = new()
    {
        { QueryPosition.SELECT,   new List<object?>() },
        { QueryPosition.FROM,     new List<object?>() },
        { QueryPosition.WHERE,    new List<object?>() },
        { QueryPosition.ORDER_BY, new List<object?>() },
        { QueryPosition.LIMIT,    new List<object?>() },
        { QueryPosition.UPDATE,   new List<object?>() },
        { QueryPosition.SET,      new List<object?>() }
    };

    public IKeyspace Keyspace { get; }
    protected Document _document { get; }

    private static readonly VaultMetadata _vaultMeta = VaultRegistry.GetByClr(typeof(TVaultModel));

    private static readonly Action<object, DateTime>? _timestampSetter =
        TimestampSetterFactory.Create(typeof(TVaultModel));

    public CqlVault(ICqlDatabaseProvider databaseProvider, IKeyspace keyspace, Document document, IServiceProvider serviceProvider)
    {
        _databaseProvider = databaseProvider;
        _document = document;
        Keyspace = keyspace;
        _serviceProvider = serviceProvider;
    }

    public async Task SaveAsync(TVaultModel entity, bool? saveHistory = false)
    {
        await SaveEntityAsync(entity, saveHistory);
    }

    public Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false) =>
        SaveEntitiesAsync(entities, saveHistory);

    public IVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
    {
        string whereClause = ConvertWherePredicateToString(predicate);
        AddToQuery(QueryPosition.WHERE, whereClause);
        EnsureProjectionSelected();
        return this;
    }

    public IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        string orderByClause = ConvertOrderByToString(keySelector);
        AddToQuery(QueryPosition.ORDER_BY, orderByClause);
        AddToQuery(QueryPosition.SELECT, orderByClause);
        return this;
    }

    public IVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        string orderByDescClause = ConvertOrderByDescendingToString(keySelector);
        AddToQuery(QueryPosition.ORDER_BY, orderByDescClause + " DESC");
        AddToQuery(QueryPosition.SELECT, orderByDescClause);
        return this;
    }

    public IVault<TVaultModel> Take(int count)
    {
        AddToQuery(QueryPosition.LIMIT, $"LIMIT {count}");
        EnsureProjectionSelected();
        return this;
    }

    public async Task<List<TVaultModel>> ToListAsync()
    {
        string query = BuildSelectQuery();
        return (await _databaseProvider.QueryAsync<TVaultModel>(query, _queryParameters[QueryPosition.SELECT])).ToList();
    }

    public async Task<TVaultModel?> FirstOrDefaultAsync()
    {
        string query = BuildSelectQuery() + " LIMIT 1";
        var result = await _databaseProvider.QueryAsync<TVaultModel>(query, _queryParameters[QueryPosition.SELECT]);
        return result.FirstOrDefault();
    }

    public async Task<TVaultModel?> FirstAsync()
    {
        string query = BuildSelectQuery() + " LIMIT 1";
        var result = await _databaseProvider.QueryAsync<TVaultModel>(query, _queryParameters[QueryPosition.SELECT]);
        return result.First();
    }

    public async Task<List<TVaultModel>> ToListAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        Where(predicate);
        return await ToListAsync();
    }

    public async Task<long> CountAsync()
    {
        var countQuery = $"SELECT COUNT(*) FROM {_document.Name}";
        var whereClause = string.Join(" AND ", _queryParts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(whereClause))
            countQuery += $" WHERE {whereClause}";

        return await _databaseProvider.ExecuteCountAsync(countQuery, _queryParameters[QueryPosition.WHERE]);
    }

    public async Task<long> UpdateAsync(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls)
    {
        var updatedProperties = ExtractSetProperties(setPropertyCalls);
        _queryParameters[QueryPosition.SET] = updatedProperties
            .Select(kv => (object)$"{kv.Key} = {ConvertToCqlValue(kv.Value)}").ToList();

        string updateQuery = BuildUpdateQuery();
        var concatenatedParameters = _queryParameters[QueryPosition.WHERE]
            .Concat(_queryParameters[QueryPosition.SET]).ToList();

        return await _databaseProvider.ExecuteAsync(updateQuery, concatenatedParameters);
    }

    public async Task<bool> DeleteAsync()
    {
        string deleteQuery = $"DELETE FROM {_document.Name}";
        var whereClause = string.Join(" AND ", _queryParts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(whereClause))
            deleteQuery += $" WHERE {whereClause}";

        long affectedRows = await _databaseProvider.ExecuteAsync(deleteQuery, _queryParameters[QueryPosition.WHERE]);
        return affectedRows > 0;
    }

    public Task<ICursor<TVaultModel>> ToCursorAsync()
    {
        throw new NotImplementedException();
    }

    public IVault<TVaultModel> Skip(int count)
    {
        throw new NotSupportedException("Cassandra does not support skipping rows. Use paging with a WHERE clause instead.");
    }

    public async Task<IEnumerable<TResult>> SelectAsync<TResult>(
        Expression<Func<TVaultModel, TResult>> selector) where TResult : class, IVaultModel
    {
        if (_queryParts[QueryPosition.SELECT].Contains("*"))
            throw new InvalidOperationException("Invalid query. SELECT * followed by other columns is not allowed.");

        foreach (var column in ConvertSelectExpressionToString(selector))
            AddToQuery(QueryPosition.SELECT, column);

        var query = BuildSelectQuery();
        return await _databaseProvider.QueryAsync<TResult>(query, _queryParameters[QueryPosition.SELECT]);
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

    public async Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        Where(predicate);

        var where = string.Join(" AND ", _queryParts[QueryPosition.WHERE]);
        var query = string.IsNullOrEmpty(where)
            ? $"SELECT COUNT(*) FROM {_document.Name}"
            : $"SELECT COUNT(*) FROM {_document.Name} WHERE {where}";

        var count = await _databaseProvider.ExecuteCountAsync(query, _queryParameters[QueryPosition.WHERE]);
        return count > 0;
    }

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

    private void EnsureProjectionSelected()
    {
        if (_queryParts[QueryPosition.SELECT].Count == 0)
        {
            AddToQuery(QueryPosition.SELECT,
                string.Join(", ", _document.Columns.Select(kvp => $"{kvp.Value} AS {kvp.Key}")));
        }
    }

    private void AddToQuery(QueryPosition position, string queryPart, object? parameter = null)
    {
        _queryParts[position].Add(queryPart);
        if (parameter is not null)
            _queryParameters[position].Add(parameter);
    }

    public string BuildSelectQuery()
    {
        var select = _queryParts[QueryPosition.SELECT].Count == 0
            ? string.Join(", ", _document.Columns.Select(kvp => $"{kvp.Value} AS {kvp.Key}"))
            : string.Join(", ", _queryParts[QueryPosition.SELECT]);

        string selectQuery = $"SELECT {select} FROM {_document.Name}";

        var where = string.Join(" AND ", _queryParts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(where))
            selectQuery += $" WHERE {where}";

        var orderBy = string.Join(", ", _queryParts[QueryPosition.ORDER_BY]);
        if (!string.IsNullOrEmpty(orderBy))
            selectQuery += $" ORDER BY {orderBy}";

        var limit = string.Join(" ", _queryParts[QueryPosition.LIMIT]);
        if (!string.IsNullOrEmpty(limit))
            selectQuery += $" {limit}";

        return selectQuery;
    }

    public string BuildUpdateQuery()
    {
        var set = string.Join(", ", _queryParts[QueryPosition.SET]);
        var updateQuery = $"UPDATE {_document.Name} SET {set}";

        var where = string.Join(" AND ", _queryParts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(where))
            updateQuery += $" WHERE {where}";

        return updateQuery;
    }

    private string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate)
    {
        if (predicate.Body is BinaryExpression be)
        {
            var left = ParseExpression(be.Left);
            var rightVal = ExpressionUtils.Evaluate(be.Right);
            var right = ConvertToCqlValue(rightVal);
            var op = GetOperator(be.NodeType);
            return $"{left} {op} {right}";
        }

        throw new NotSupportedException("Unsupported expression type in WHERE clause.");
    }

    private string ParseExpression(Expression expression)
    {
        if (expression is MemberExpression me)
        {
            if (_document.Columns.TryGetValue(me.Member.Name, out var column))
                return column;
            return Document.ToCamelCase(me.Member.Name);
        }

        throw new NotSupportedException("Unsupported expression type.");
    }

    private static string GetOperator(ExpressionType expressionType) =>
        expressionType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.LessThan => "<",
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

    private string BuildInsertQuery(string tableName, IEnumerable<string> columns) =>
        $"INSERT INTO {tableName} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(_ => "?"))})";

    private string BuildHistoryQuery(string tableName, IEnumerable<string> columns) =>
        $"INSERT INTO {tableName}_history ({string.Join(", ", columns)}, timestamp) VALUES ({string.Join(", ", columns.Select(_ => "?"))}, ?)";

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
