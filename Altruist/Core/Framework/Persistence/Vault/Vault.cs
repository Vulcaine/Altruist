using System.Linq.Expressions;
using Altruist.Contracts;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Altruist.Database;

public class VaultAdapter<TVaultModel> : IVault<TVaultModel> where TVaultModel : class, IVaultModel
{
    private readonly IVault<TVaultModel> _underlying;

    public VaultAdapter(IDatabaseVaultFactory vaultMaker, IKeyspace keyspace)
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

    public Task SaveAsync(object entity, bool? saveHistory = false)
    {
        return _underlying.SaveAsync(entity, saveHistory);
    }

    public Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false)
    {
        return _underlying.SaveBatchAsync(entities, saveHistory);
    }

    public Task SaveBatchAsync(IEnumerable<object> entities, bool? saveHistory = false)
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

    public ITypeErasedVault Select(Type type)
    {
        // Ensure the type implements IVaultModel
        if (!typeof(IVaultModel).IsAssignableFrom(type))
        {
            throw new InvalidOperationException($"Type {type.Name} does not implement IVaultModel.");
        }
        var vaultInterfaceType = typeof(VaultAdapter<>).MakeGenericType(type);
        var vault = _serviceProvider.GetService(vaultInterfaceType);
        return (ITypeErasedVault)vault!;
    }


    // This is the only generic method, never called directly
    protected IVault<IVaultModel> SelectGeneric<T>() where T : class, IVaultModel
    {
        _databaseProvider.ChangeKeyspaceAsync(_keyspace.Name);
        return (IVault<IVaultModel>)_serviceProvider.GetRequiredService<IVault<T>>();
    }
}

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
        var document = Document.From(typeof(TVaultModel));
        if (_databaseProvider is ICqlDatabaseProvider cqlDatabaseProvider)
        {
            return new CqlVault<TVaultModel>(cqlDatabaseProvider, keyspace, document);
        }
        else if (_databaseProvider is ILinqDatabaseProvider linqDatabaseProvider)
        {
            return new LinqVault<TVaultModel>(linqDatabaseProvider, keyspace, document);
        }
        else
        {
            throw new NotSupportedException($"Cannot create vault. Unsupported database provider {_databaseProvider.GetType().FullName}. If you got a custom provider, make sure you've overridden the DatabaseVaultFactory implementation.");
        }
    }
}