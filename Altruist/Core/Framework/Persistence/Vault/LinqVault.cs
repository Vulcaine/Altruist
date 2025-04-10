using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Altruist.Database;

public class LinqVault<TVaultModel> : ILinqVault<TVaultModel> where TVaultModel : class, IVaultModel
{
    private readonly ILinqDatabaseProvider _databaseProvider;
    private IQueryable<TVaultModel> _query;

    public IKeyspace Keyspace { get; }

    protected Document _document;

    public LinqVault(ILinqDatabaseProvider databaseProvider, IKeyspace keyspace, Document document)
    {
        _document = document;
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

    public async Task SaveAsync(TVaultModel entity, bool? saveHistory = false)
    {
        _databaseProvider.Context.Add(entity);
        await _databaseProvider.Context.SaveChangesAsync();
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

    public Task SaveAsync(object entity, bool? saveHistory = false)
    {
        throw new NotImplementedException();
    }

    public Task SaveBatchAsync(IEnumerable<object> entities, bool? saveHistory = false)
    {
        throw new NotImplementedException();
    }
}
