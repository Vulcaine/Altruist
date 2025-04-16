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

    public async Task<long> CountAsync()
    {
        return await Task.FromResult(_query.Count());
    }

    public async Task<long> UpdateAsync(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls)
    {
        return await _query.ExecuteUpdateAsync(setPropertyCalls);
    }

    public async Task SaveAsync(TVaultModel entity, bool? saveHistory = false)
    {
        await SaveEntityAsync(entity);
    }

    public async Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false)
    {
        await SaveEntityBatchAsync(entities);
    }

    public async Task SaveAsync(object entity, bool? saveHistory = false)
    {
        if (entity is TVaultModel typedEntity)
        {
            await SaveEntityAsync(typedEntity);
        }
        else
        {
            throw new InvalidCastException($"Expected entity of type {typeof(TVaultModel).Name}, got {entity.GetType().Name}");
        }
    }

    public async Task SaveBatchAsync(IEnumerable<object> entities, bool? saveHistory = false)
    {
        var typedEntities = entities
            .OfType<TVaultModel>()
            .ToList();

        if (typedEntities.Count != entities.Count())
        {
            throw new InvalidCastException("One or more entities are not of the expected type.");
        }

        await SaveEntityBatchAsync(typedEntities);
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

    private async Task SaveEntityAsync(TVaultModel entity)
    {
        if (entity is IBeforeVaultSave beforeHook && !await beforeHook.BeforeSaveAsync())
            return;

        _databaseProvider.Context.Add(entity);
        await _databaseProvider.Context.SaveChangesAsync();

        if (entity is IAfterVaultSave afterHook)
            await afterHook.AfterSaveAsync();
    }

    private async Task SaveEntityBatchAsync(IEnumerable<TVaultModel> entities)
    {
        var filteredEntities = new List<TVaultModel>();

        foreach (var entity in entities)
        {
            if (entity is IBeforeVaultSave beforeHook)
            {
                var proceed = await beforeHook.BeforeSaveAsync();
                if (!proceed) continue;
            }

            filteredEntities.Add(entity);
        }

        await _databaseProvider.Context.AddRangeAsync(filteredEntities);
        await _databaseProvider.Context.SaveChangesAsync();

        foreach (var entity in filteredEntities)
        {
            if (entity is IAfterVaultSave afterHook)
                await afterHook.AfterSaveAsync();
        }
    }

    public Task<ICursor<TVaultModel>> ToCursorAsync()
    {
        throw new NotImplementedException();
    }
}
