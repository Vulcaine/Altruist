using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore.Query;

namespace Altruist.Redis;


public class RedisVault<TVaultModel> : IVault<TVaultModel> where TVaultModel : class, IVaultModel
{
    public IKeyspace Keyspace => throw new NotImplementedException();

    public Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        throw new NotImplementedException();
    }

    public Task<int> CountAsync()
    {
        throw new NotImplementedException();
    }

    public Task<bool> DeleteAsync()
    {
        throw new NotImplementedException();
    }

    public Task<int> DeleteWhereAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        throw new NotImplementedException();
    }

    public Task<TVaultModel?> FirstAsync()
    {
        throw new NotImplementedException();
    }

    public Task<TVaultModel?> FirstOrDefaultAsync()
    {
        throw new NotImplementedException();
    }

    public IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        throw new NotImplementedException();
    }

    public IVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector)
    {
        throw new NotImplementedException();
    }

    public Task SaveAsync(TVaultModel entity, bool? saveHistory = false)
    {
        throw new NotImplementedException();
    }

    public Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false)
    {
        throw new NotImplementedException();
    }

    public Task<IEnumerable<TResult>> SelectAsync<TResult>(Expression<Func<TVaultModel, TResult>> selector) where TResult : class, IVaultModel
    {
        throw new NotImplementedException();
    }

    public IVault<TVaultModel> Skip(int count)
    {
        throw new NotImplementedException();
    }

    public IVault<TVaultModel> Take(int count)
    {
        throw new NotImplementedException();
    }

    public Task<List<TVaultModel>> ToListAsync()
    {
        throw new NotImplementedException();
    }

    public Task<List<TVaultModel>> ToListAsync(Expression<Func<TVaultModel, bool>> predicate)
    {
        throw new NotImplementedException();
    }

    public Task<int> UpdateAsync(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls)
    {
        throw new NotImplementedException();
    }

    public IVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate)
    {
        throw new NotImplementedException();
    }
}