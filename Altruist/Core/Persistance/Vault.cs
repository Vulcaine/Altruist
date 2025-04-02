using System.Linq.Expressions;
using System.Reflection;
using Altruist.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;
using Altruist.UORM;
using Microsoft.AspNetCore.Mvc;
using Altruist.Redis;

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


public class LinqVault<TVaultModel> : ILinqVault<TVaultModel> where TVaultModel : class, IVaultModel
{
    private readonly ILinqDatabaseProvider _databaseProvider;
    private IQueryable<TVaultModel> _query;

    public IKeyspace Keyspace { get; }

    public LinqVault(ILinqDatabaseProvider databaseProvider, IKeyspace keyspace)
    {
        _databaseProvider = databaseProvider;
        _query = _databaseProvider.QueryAsync<TVaultModel>(x => true).Result.AsQueryable();
        Keyspace = keyspace;
    }

    public IVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
    {
        _query = _query.Where(predicate);
        return this;
    }

    public IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        _query = _query.OrderBy(keySelector);
        return this;
    }

    public IVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        _query = _query.OrderByDescending(keySelector);
        return this;
    }

    public IVault<TVaultModel> Take(int count)
    {
        _query = _query.Take(count);
        return this;
    }

    public async Task<List<TVaultModel>> ToListAsync()
    {
        return await Task.FromResult(_query.ToList());
    }

    public async Task<TVaultModel?> FirstOrDefaultAsync()
    {
        return await Task.FromResult(_query.FirstOrDefault());
    }

    public async Task<TVaultModel?> FirstAsync()
    {
        return await Task.FromResult(_query.First());
    }

    public async Task<List<TVaultModel>> ToListAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        var filteredQuery = _query.Where(predicate);
        return await Task.FromResult(filteredQuery.ToList());
    }

    public async Task<int> CountAsync()
    {
        return await Task.FromResult(_query.Count());
    }

    public async Task<int> UpdateAsync(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls)
    {
        return await _query.ExecuteUpdateAsync(setPropertyCalls);
    }

    public async Task SaveAsync(TVaultModel entity)
    {
        _databaseProvider.Context.Add(entity);
        await _databaseProvider.Context.SaveChangesAsync();
    }

    public async Task SaveBatchAsync(IEnumerable<TVaultModel> entities)
    {
        await _databaseProvider.Context.AddRangeAsync(entities);
        await _databaseProvider.Context.SaveChangesAsync();
    }

    public Task SaveAsync(TVaultModel entity, bool? saveHistory = false)
    {
        throw new NotImplementedException();
    }

    public Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false)
    {
        throw new NotImplementedException();
    }

    public async Task<bool> DeleteAsync()
    {
        int affectedRows = await _query.ExecuteDeleteAsync();
        return affectedRows > 0;
    }

    public async Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        return await _query.AnyAsync(predicate);
    }


    public IVault<TVaultModel> Skip(int count)
    {
        _query = _query.Skip(count);
        return this;
    }


    public async Task<IEnumerable<TResult>> SelectAsync<TResult>(Expression<Func<TVaultModel, TResult>> selector) where TResult : class, IVaultModel
    {
        return await _query.Select(selector).ToListAsync();
    }
}

public class CqlVault<TVaultModel> : ICqlVault<TVaultModel> where TVaultModel : class, IVaultModel
{
    private readonly ICqlDatabaseProvider _databaseProvider;
    private Dictionary<QueryPosition, List<string>> _queryParts;
    private Dictionary<QueryPosition, List<object>> _queryParameters;

    public IKeyspace Keyspace { get; }

    public CqlVault(ICqlDatabaseProvider databaseProvider, IKeyspace keyspace)
    {
        _databaseProvider = databaseProvider;
        _queryParts = new Dictionary<QueryPosition, List<string>>()
        {
            { QueryPosition.SELECT, new List<string>() },
            { QueryPosition.FROM, new List<string>() },
            { QueryPosition.WHERE, new List<string>() },
            { QueryPosition.ORDER_BY, new List<string>() },
            { QueryPosition.LIMIT, new List<string>() },
            { QueryPosition.UPDATE, new List<string>() },
            { QueryPosition.SET, new List<string>() }
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

    public async Task SaveAsync(TVaultModel entity, bool? saveHistory = false)
    {
        entity.Timestamp = DateTime.UtcNow;
        var tableAttribute = typeof(TVaultModel).GetCustomAttribute<TableAttribute>();

        var columns = string.Join(", ", _queryParts[QueryPosition.SET]);
        var placeholders = string.Join(", ", _queryParameters[QueryPosition.SET].Select(param => "?"));
        var parameters = _queryParameters[QueryPosition.SET].ToArray();

        var insertQuery = $"INSERT INTO {typeof(TVaultModel).Name} ({columns}) VALUES ({placeholders}) IF NOT EXISTS;";

        var batchQueries = new List<string> { insertQuery };

        if (tableAttribute?.StoreHistory == true && saveHistory == true)
        {
            var historyColumns = string.Join(", ", _queryParts[QueryPosition.SET]);
            var historyPlaceholders = string.Join(", ", _queryParameters[QueryPosition.SET].Select(param => "?"));
            var historyQuery = $"INSERT INTO {typeof(TVaultModel).Name}_history ({historyColumns}, timestamp) VALUES ({historyPlaceholders}, ?);";

            var parametersForHistory = _queryParameters[QueryPosition.SET].ToList();
            parametersForHistory.Add(entity.Timestamp);

            batchQueries.Add(historyQuery);
            parameters = parameters.Concat(parametersForHistory).ToArray();
        }
        else if (saveHistory == true)
        {
            throw new Exception($"History is not enabled for the table {typeof(TVaultModel).Name}. Consider adding StoreHistory=true");
        }

        var batchQuery = $"BEGIN BATCH {string.Join(" ", batchQueries)} APPLY BATCH;";
        await _databaseProvider.ExecuteAsync(batchQuery, parameters);
    }


    public async Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false)
    {
        var tableAttribute = typeof(TVaultModel).GetCustomAttribute<TableAttribute>();

        var columnNames = string.Join(", ", _queryParts[QueryPosition.SET]);
        var valuePlaceholders = string.Join(", ", _queryParameters[QueryPosition.SET].Select(p => "?"));

        var batchQueries = new List<string>();

        foreach (var entity in entities)
        {
            var insertQuery = $"INSERT INTO {typeof(TVaultModel).Name} ({columnNames}) VALUES ({valuePlaceholders})";
            batchQueries.Add(insertQuery);
            if (tableAttribute?.StoreHistory == true)
            {
                entity.Timestamp = DateTime.UtcNow;
                var historyQuery = $"INSERT INTO {typeof(TVaultModel).Name}_history ({columnNames}, timestamp) VALUES ({valuePlaceholders}, ?)";
                batchQueries.Add(historyQuery);
            }
        }

        var upsertBatchQuery = $"BEGIN BATCH {string.Join(" ", batchQueries)} APPLY BATCH;";

        var parameters = entities.Select(entity =>
        {
            var parametersForEntity = _queryParameters[QueryPosition.SET].ToArray();

            if (tableAttribute?.StoreHistory == true)
            {
                var parametersForHistory = parametersForEntity.ToList();
                parametersForHistory.Add(entity.Timestamp);
                return parametersForHistory.ToArray();
            }

            return parametersForEntity;
        }).SelectMany(p => p).ToArray();

        await _databaseProvider.ExecuteAsync(upsertBatchQuery, parameters);
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
        return this;
    }

    public IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        string orderByClause = ConvertOrderByToString(keySelector);
        AddToQuery(QueryPosition.ORDER_BY, orderByClause);
        return this;
    }

    public IVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        string orderByDescClause = ConvertOrderByDescendingToString(keySelector);
        AddToQuery(QueryPosition.ORDER_BY, orderByDescClause);
        return this;
    }

    public IVault<TVaultModel> Take(int count)
    {
        AddToQuery(QueryPosition.LIMIT, $"LIMIT {count}");
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


    private string BuildSelectQuery()
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

    private string BuildUpdateQuery()
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
            return constantExpression.Value!.ToString()!;
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
            return $"{memberExpression.Member.Name} DESC";
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
        string selectedColumns = ConvertSelectExpressionToString(selector);
        AddToQuery(QueryPosition.SELECT, selectedColumns);

        string query = BuildSelectQuery();
        return await _databaseProvider.QueryAsync<TResult>(query, _queryParameters[QueryPosition.SELECT]);
    }

    private string ConvertSelectExpressionToString<TResult>(Expression<Func<TVaultModel, TResult>> selector)
    {
        if (selector.Body is NewExpression newExpression)
        {
            return string.Join(", ", newExpression.Members!.Select(m => m.Name));
        }

        throw new NotSupportedException("Unsupported expression type for SELECT.");
    }

}


public class Vault<TVaultModel> : IVault<TVaultModel> where TVaultModel : class, IVaultModel
{
    private readonly IVault<TVaultModel> _underlying;

    public Vault(IDatabaseVaultFactory vaultMaker, IKeyspace keyspace)
    {
        _underlying = vaultMaker.Make<TVaultModel>(keyspace);
        Keyspace = keyspace;
    }

    public IKeyspace Keyspace { get; }

    public Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        return _underlying.AnyAsync(predicate);
    }

    public Task<int> CountAsync()
    {
        return _underlying.CountAsync();
    }

    public Task<bool> DeleteAsync()
    {
        return _underlying.DeleteAsync();
    }

    public Task<TVaultModel?> FirstAsync()
    {
        return _underlying.FirstAsync();
    }

    public Task<TVaultModel?> FirstOrDefaultAsync()
    {
        return _underlying.FirstOrDefaultAsync();
    }

    public IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        return _underlying.OrderBy(keySelector);
    }

    public IVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        return _underlying.OrderByDescending(keySelector);
    }

    public Task SaveAsync(TVaultModel entity, bool? saveHistory = false)
    {
        return _underlying.SaveAsync(entity, saveHistory);
    }

    public Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false)
    {
        return _underlying.SaveBatchAsync(entities, saveHistory);
    }

    public Task<IEnumerable<TResult>> SelectAsync<TResult>(Expression<Func<TVaultModel, TResult>> selector) where TResult : class, IVaultModel
    {
        return _underlying.SelectAsync(selector);
    }

    public IVault<TVaultModel> Skip(int count)
    {
        return _underlying.Skip(count);
    }

    public IVault<TVaultModel> Take(int count)
    {
        return _underlying.Take(count);
    }

    public Task<List<TVaultModel>> ToListAsync()
    {
        return _underlying.ToListAsync();
    }

    public Task<List<TVaultModel>> ToListAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        return _underlying.ToListAsync(predicate);
    }

    public Task<int> UpdateAsync(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls)
    {
        return _underlying.UpdateAsync(setPropertyCalls);
    }

    public IVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
    {
        return _underlying.Where(predicate);
    }
}


/// <summary>
/// Repository defining a set of vaults and the keyspace they belong to
/// </summary>
public abstract class VaultRepository<TKeyspace> : IVaultRepository<TKeyspace> where TKeyspace : class, IKeyspace
{
    private TKeyspace _keyspace;
    private IGeneralDatabaseProvider _databaseProvider;
    private IServiceProvider _serviceProvider;

    public IDatabaseServiceToken Token { get; }

    public VaultRepository(IServiceProvider provider, IGeneralDatabaseProvider databaseProvider, TKeyspace keyspace)
    {
        _serviceProvider = provider;
        _keyspace = keyspace;
        _databaseProvider = databaseProvider;
        Token = _databaseProvider.Token;
    }

    public IVault<TVaultModel> Select<TVaultModel>() where TVaultModel : class, IVaultModel
    {
        var vault = _serviceProvider.GetService<IVault<TVaultModel>>();

        if (vault == null)
        {
            throw new InvalidOperationException($"Vault for type {typeof(TKeyspace).Name} is not registered.");
        }

        _databaseProvider.ChangeKeyspaceAsync(_keyspace.Name);
        return vault;
    }

    public IVault<IVaultModel> Select(Type type)
    {
        if (!type.IsAssignableTo(typeof(IVaultModel)))
        {
            throw new InvalidOperationException($"Type {type.Name} does not implement IVaultModel.");
        }

        var vault = _serviceProvider.GetService(type);

        if (vault == null)
        {
            throw new InvalidOperationException($"Vault for type {typeof(TKeyspace).Name} is not registered.");
        }

        _databaseProvider.ChangeKeyspaceAsync(_keyspace.Name);
        return (vault as IVault<IVaultModel>)!;
    }
}

// public abstract class CacheVaultFactory : ICacheVaultFactory
// {
//     private readonly ICacheProvider _cacheProvider;
//     public ICacheServiceToken Token => throw new NotImplementedException();

//     public CacheVaultFactory(ICacheProvider cacheProvider) => _cacheProvider = cacheProvider;

//     IVault<TVaultModel> ICacheVaultFactory.Make<TVaultModel>()
//     {
//         if (_cacheProvider is RedisCacheProvider redisCacheProvider)
//         {
//             return new RedisVault<TVaultModel>(redisCacheProvider);
//         }
//         else
//         {
//             throw new NotSupportedException($"Cannot create vault. Unsupported cache provider {_cacheProvider.GetType().FullName}. If you got a custom provider, make sure you've overridden the CacheVaultFactory implementation.");
//         }
//     }
// }

public abstract class DatabaseVaultFactory : IDatabaseVaultFactory
{
    private readonly IGeneralDatabaseProvider _databaseProvider;

    public IDatabaseServiceToken Token => _databaseProvider.Token;

    public DatabaseVaultFactory(
        IGeneralDatabaseProvider databaseProvider)
    {
        _databaseProvider = databaseProvider;
    }

    public virtual IVault<TVaultModel> Make<TVaultModel>(IKeyspace keyspace) where TVaultModel : class, IVaultModel
    {
        if (_databaseProvider is ICqlDatabaseProvider cqlDatabaseProvider)
        {
            return new CqlVault<TVaultModel>(cqlDatabaseProvider, keyspace);
        }
        else if (_databaseProvider is ILinqDatabaseProvider linqDatabaseProvider)
        {
            return new LinqVault<TVaultModel>(linqDatabaseProvider, keyspace);
        }
        else
        {
            throw new NotSupportedException($"Cannot create vault. Unsupported database provider {_databaseProvider.GetType().FullName}. If you got a custom provider, make sure you've overridden the DatabaseVaultFactory implementation.");
        }
    }
}