using System.Linq.Expressions;
using System.Reflection;
using Altruist.UORM;
using Microsoft.EntityFrameworkCore.Query;

namespace Altruist.Database;

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
    private readonly ICqlDatabaseProvider _databaseProvider;
    private Dictionary<QueryPosition, HashSet<string>> _queryParts;
    private Dictionary<QueryPosition, List<object>> _queryParameters;

    public IKeyspace Keyspace { get; }

    protected Document _document { get; }

    public CqlVault(ICqlDatabaseProvider databaseProvider, IKeyspace keyspace, Document document)
    {
        _document = document;
        _databaseProvider = databaseProvider;
        _queryParts = new Dictionary<QueryPosition, HashSet<string>>()
        {
            { QueryPosition.SELECT, new HashSet<string>() },
            { QueryPosition.FROM, new HashSet<string>() },
            { QueryPosition.WHERE, new HashSet<string>() },
            { QueryPosition.ORDER_BY, new HashSet<string>() },
            { QueryPosition.LIMIT, new HashSet<string>() },
            { QueryPosition.UPDATE, new HashSet<string>() },
            { QueryPosition.SET, new HashSet<string>() }
        };

        _queryParameters = new Dictionary<QueryPosition, List<object>>()
        {
            { QueryPosition.SELECT, new List<object>() },
            { QueryPosition.FROM, new List<object>() },
            { QueryPosition.WHERE, new List<object>() },
            { QueryPosition.ORDER_BY, new List<object>() },
            { QueryPosition.LIMIT, new List<object>() },
            { QueryPosition.UPDATE, new List<object>() },
            { QueryPosition.SET, new List<object>() }
        };

        Keyspace = keyspace;
    }

    public Task SaveAsync(TVaultModel entity, bool? saveHistory = false) =>
        SaveEntityAsync(entity, saveHistory, false);

    public Task SaveAsync(object entity, bool? saveHistory = false) =>
        SaveEntityAsync(entity, saveHistory);

    public Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false) =>
        SaveEntitiesAsync(entities.Cast<object>(), saveHistory);

    public Task SaveBatchAsync(IEnumerable<object> entities, bool? saveHistory = false) =>
        SaveEntitiesAsync(entities, saveHistory);


    private void ValidateEntityType(Type entityType)
    {
        if (!typeof(IVaultModel).IsAssignableFrom(entityType))
        {
            throw new InvalidOperationException($"Entity type {entityType.Name} does not implement IVaultModel.");
        }
    }

    private string BuildInsertQuery(string tableName, IEnumerable<string> columns) =>
        $"INSERT INTO {tableName} ({string.Join(", ", columns)}) VALUES ({string.Join(", ", columns.Select(_ => "?"))})";

    private string BuildHistoryQuery(string tableName, IEnumerable<string> columns) =>
        $"INSERT INTO {tableName}_history ({string.Join(", ", columns)}, timestamp) VALUES ({string.Join(", ", columns.Select(_ => "?"))}, ?)";

    private object?[] GetParameterValues(object entity, IEnumerable<string> fields, bool includeTimestamp = false)
    {
        var values = fields.Select(field => _document.PropertyAccessors[field](entity)).ToList();

        if (includeTimestamp)
        {
            var now = DateTime.UtcNow;
            var prop = entity.GetType().GetProperty("Timestamp");
            prop?.SetValue(entity, now);
            values.Add(now);
        }

        return values.ToArray();
    }

    private async Task SaveEntityAsync(object entity, bool? saveHistory = false, bool? validate = false)
    {
        if (entity == null) throw new ArgumentNullException(nameof(entity));

        var type = entity.GetType();
        if (validate == true) ValidateEntityType(type);

        var tableAttr = type.GetCustomAttribute<VaultAttribute>();
        var tableName = tableAttr?.Name ?? type.Name;
        var fields = _document.Fields;

        var insertQuery = BuildInsertQuery(tableName, fields);
        var parameters = GetParameterValues(entity, fields, false).ToList();

        var queries = new List<string> { insertQuery };

        if (tableAttr?.StoreHistory == true && saveHistory == true)
        {
            var historyQuery = BuildHistoryQuery(tableName, fields);
            queries.Add(historyQuery);

            var historyParams = GetParameterValues(entity, fields, true);
            parameters.AddRange(historyParams.Skip(fields.Count()));
        }
        else if (saveHistory == true)
        {
            throw new Exception($"History is not enabled for the table {tableName}. Consider adding StoreHistory=true");
        }

        var batchQuery = $"BEGIN BATCH {string.Join(" ", queries)} APPLY BATCH;";
        await _databaseProvider.ExecuteAsync(batchQuery, parameters.ToArray()!);
    }


    private async Task SaveEntitiesAsync(IEnumerable<object> entities, bool? saveHistory = false)
    {
        if (entities == null || !entities.Any())
            throw new ArgumentException("Entities cannot be null or empty.");

        var type = entities.First().GetType();
        ValidateEntityType(type);

        var tableAttr = type.GetCustomAttribute<VaultAttribute>();
        var tableName = tableAttr?.Name ?? type.Name;
        var fields = _document.Fields;

        var insertQuery = BuildInsertQuery(tableName, fields);
        var historyQuery = BuildHistoryQuery(tableName, fields);

        var queries = new List<string>();
        var allParams = new List<object?>();

        foreach (var entity in entities)
        {
            queries.Add(insertQuery);
            allParams.AddRange(GetParameterValues(entity, fields, false));

            if (tableAttr?.StoreHistory == true)
            {
                queries.Add(historyQuery);
                allParams.AddRange(GetParameterValues(entity, fields, true).Skip(fields.Count()));
            }
            else if (saveHistory == true)
            {
                throw new Exception($"History is not enabled for the table {tableName}. Consider adding StoreHistory=true");
            }
        }

        var batchQuery = $"BEGIN BATCH {string.Join(" ", queries)} APPLY BATCH;";
        await _databaseProvider.ExecuteAsync(batchQuery, allParams.ToArray()!);
    }


    private void AddToQuery(QueryPosition position, string queryPart, object parameter = null!)
    {
        if (!_queryParts.ContainsKey(position))
            throw new ArgumentOutOfRangeException($"Invalid query position: {position}");

        _queryParts[position].Add(queryPart);
        if (parameter != null)
        {
            _queryParameters[position].Add(parameter);
        }
    }

    public IVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
    {
        string whereClause = ConvertWherePredicateToString(predicate);
        AddToQuery(QueryPosition.WHERE, whereClause);
        if (_queryParts[QueryPosition.SELECT].Count == 0)
        {
            AddToQuery(QueryPosition.SELECT, "*");
        }
        return this;
    }

    public IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        string orderByClause = ConvertOrderByToString(keySelector);
        AddToQuery(QueryPosition.ORDER_BY, orderByClause);
        // we have to make sure the ordered property is selected
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
        if (_queryParts[QueryPosition.SELECT].Count == 0)
        {
            AddToQuery(QueryPosition.SELECT, "*");
        }
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

    public async Task<int> CountAsync()
    {
        string countQuery = $"SELECT COUNT(*) FROM {typeof(TVaultModel).Name}";
        string whereClause = string.Join(" AND ", _queryParts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(whereClause))
        {
            countQuery += $" WHERE {whereClause}";
        }

        return await _databaseProvider.ExecuteAsync(countQuery, _queryParameters[QueryPosition.WHERE]);
    }

    public async Task<int> UpdateAsync(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls)
    {
        var setClauseParts = new List<object>();
        var updatedProperties = ExtractSetProperties(setPropertyCalls);

        foreach (var property in updatedProperties)
        {
            setClauseParts.Add($"{property.Key} = {property.Value}");
        }

        _queryParameters[QueryPosition.SET] = setClauseParts;

        string updateQuery = BuildUpdateQuery();
        var concatenatedParameters = _queryParameters[QueryPosition.WHERE]
            .Concat(_queryParameters[QueryPosition.SET])
            .ToArray();

        return await _databaseProvider.ExecuteAsync(updateQuery, concatenatedParameters);
    }

    private Dictionary<string, object> ExtractSetProperties(
        Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls)
    {
        var updatedProperties = new Dictionary<string, object>();

        if (setPropertyCalls.Body is MemberInitExpression memberInit)
        {
            foreach (var binding in memberInit.Bindings.OfType<MemberAssignment>())
            {
                string propertyName = binding.Member.Name;
                object propertyValue = Expression.Lambda(binding.Expression).Compile().DynamicInvoke()!;

                updatedProperties[propertyName] = propertyValue;
            }
        }

        return updatedProperties;
    }


    public string BuildSelectQuery()
    {
        string selectQuery = $"SELECT {string.Join(", ", _queryParts[QueryPosition.SELECT])} FROM {typeof(TVaultModel).Name}";

        string whereClause = string.Join(" AND ", _queryParts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(whereClause))
        {
            selectQuery += $" WHERE {whereClause}";
        }

        string orderByClause = string.Join(", ", _queryParts[QueryPosition.ORDER_BY]);
        if (!string.IsNullOrEmpty(orderByClause))
        {
            selectQuery += $" ORDER BY {orderByClause}";
        }

        string limitClause = string.Join(" ", _queryParts[QueryPosition.LIMIT]);
        if (!string.IsNullOrEmpty(limitClause))
        {
            selectQuery += $" {limitClause}";
        }

        return selectQuery;
    }

    public string BuildUpdateQuery()
    {
        string updateQuery = $"UPDATE {typeof(TVaultModel).Name} SET {string.Join(", ", _queryParts[QueryPosition.SET])}";

        string whereClause = string.Join(" AND ", _queryParts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(whereClause))
        {
            updateQuery += $" WHERE {whereClause}";
        }

        return updateQuery;
    }

    private string ConvertWherePredicateToString(Expression<Func<TVaultModel, bool>> predicate)
    {
        if (predicate.Body is BinaryExpression binaryExpression)
        {
            var left = ParseExpression(binaryExpression.Left);
            var right = ParseExpression(binaryExpression.Right);
            string @operator = GetOperator(binaryExpression.NodeType);

            return $"{left} {@operator} {right}";
        }

        throw new NotSupportedException("Unsupported expression type in WHERE clause.");
    }

    private string ParseExpression(Expression expression)
    {
        if (expression is MemberExpression memberExpression)
        {
            return memberExpression.Member.Name;
        }

        if (expression is ConstantExpression constantExpression && constantExpression.Value != null)
        {
            var value = constantExpression.Value;
            switch (value)
            {
                case string stringValue:
                    return $"'{stringValue}'";
                case bool boolValue:
                    return boolValue ? "TRUE" : "FALSE";
                case DateTime dateTimeValue:
                    return $"'{dateTimeValue:yyyy-MM-dd HH:mm:ss}'";
                case null:
                    return "NULL";
                default:
                    return value.ToString()!;
            }
        }

        throw new NotSupportedException("Unsupported expression type.");
    }

    private string GetOperator(ExpressionType expressionType)
    {
        return expressionType switch
        {
            ExpressionType.Equal => "=",
            ExpressionType.GreaterThan => ">",
            ExpressionType.LessThan => "<",
            ExpressionType.AndAlso => "AND",
            ExpressionType.OrElse => "OR",
            _ => throw new NotSupportedException($"Unsupported operator: {expressionType}")
        };
    }

    private string ConvertOrderByToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        if (keySelector.Body is MemberExpression memberExpression)
        {
            return memberExpression.Member.Name;
        }

        throw new NotSupportedException("Unsupported expression type in ORDER BY clause.");
    }

    private string ConvertOrderByDescendingToString<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        if (keySelector.Body is MemberExpression memberExpression)
        {
            return $"{memberExpression.Member.Name}";
        }

        throw new NotSupportedException("Unsupported expression type in ORDER BY DESC clause.");
    }

    private string ConvertSetClauseToString(Expression<Func<TVaultModel, TVaultModel>> updatedValues)
    {
        var bindings = new List<string>();

        if (updatedValues.Body is MemberInitExpression memberInit)
        {
            foreach (var binding in memberInit.Bindings)
            {
                if (binding is MemberAssignment memberAssignment)
                {
                    string column = memberAssignment.Member.Name;
                    string value = ParseExpression(memberAssignment.Expression); // Assuming it's a simple assignment
                    bindings.Add($"{column} = {value}");
                }
            }
        }

        return string.Join(", ", bindings);
    }

    public async Task<bool> DeleteAsync()
    {
        string deleteQuery = $"DELETE FROM {typeof(TVaultModel).Name}";

        string whereClause = string.Join(" AND ", _queryParts[QueryPosition.WHERE]);
        if (!string.IsNullOrEmpty(whereClause))
        {
            deleteQuery += $" WHERE {whereClause}";
        }

        int affectedRows = await _databaseProvider.ExecuteAsync(deleteQuery, _queryParameters[QueryPosition.WHERE]);
        return affectedRows > 0;
    }

    public async Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        Where(predicate); // Reuse existing Where() method

        string query = $"SELECT COUNT(*) FROM {typeof(TVaultModel).Name} WHERE {string.Join(" AND ", _queryParts[QueryPosition.WHERE])}";
        int count = await _databaseProvider.ExecuteAsync(query, _queryParameters[QueryPosition.WHERE]);

        return count > 0;
    }


    public IVault<TVaultModel> Skip(int count)
    {
        // No native OFFSET in Cassandra, must be handled at the application level
        throw new NotSupportedException("Cassandra does not support skipping rows. Use paging with a WHERE clause instead.");
    }


    public async Task<IEnumerable<TResult>> SelectAsync<TResult>(Expression<Func<TVaultModel, TResult>> selector) where TResult : class, IVaultModel
    {
        if (_queryParts[QueryPosition.SELECT].Contains("*"))
        {
            throw new InvalidOperationException("Invalid query. SELECT * followed by other columns is not allowed.");
        }

        IEnumerable<string> selectedColumns = ConvertSelectExpressionToString(selector);
        foreach (var column in selectedColumns)
        {
            AddToQuery(QueryPosition.SELECT, column);
        }

        string query = BuildSelectQuery();
        return await _databaseProvider.QueryAsync<TResult>(query, _queryParameters[QueryPosition.SELECT]);
    }

    private IEnumerable<string> ConvertSelectExpressionToString<TResult>(Expression<Func<TVaultModel, TResult>> selector)
    {
        if (selector.Body is NewExpression newExpression)
        {
            return newExpression.Members!.Select(m => m.Name);
        }

        throw new NotSupportedException("Unsupported expression type for SELECT.");
    }

}
