using System.Linq.Expressions;
using Altruist.Contracts;
using Altruist.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Altruist;

public interface IKeyspace
{
    string Name { get; }
}

public interface IModel
{
    public string Type { get; set; }
}

public interface IVaultFactory<TToken, TConfig> where TConfig : IConfiguration where TToken : IServiceToken<TConfig>
{
    public TToken Token { get; }
}

public interface IDatabaseVaultFactory : IVaultFactory<IDatabaseServiceToken, IDatabaseConfiguration>
{
    IVault<TVaultModel> Make<TVaultModel>(IKeyspace keyspace) where TVaultModel : class, IVaultModel;
}

public interface ICacheVaultFactory : IVaultFactory<ICacheServiceToken, ICacheConfiguration>
{
    IVault<TVaultModel> Make<TVaultModel>() where TVaultModel : class, IVaultModel;
}

public interface IVaultModel : IModel
{
    public string Id { get; set; }
    DateTime Timestamp { get; set; }
}

public interface ILinqVault<TVaultModel> : IVault<TVaultModel> where TVaultModel : class, IVaultModel
{

}

public interface IVaultRepository<TKeyspace> where TKeyspace : class, IKeyspace
{
    IDatabaseServiceToken Token { get; }
    IVault<TVaultModel> Select<TVaultModel>() where TVaultModel : class, IVaultModel;
    ITypeErasedVault Select(Type type);
}

public interface IGeneralDatabaseProvider
{
    bool IsConnected { get; }
    event Action? OnConnected;
    event Action<Exception> OnFailed;
    IDatabaseServiceToken Token { get; }
    Task CreateTableAsync<TVaultModel>(IKeyspace? keyspace = null) where TVaultModel : class, IVaultModel;
    Task CreateTableAsync(Type entityType, IKeyspace? keyspace = null);
    Task CreateKeySpaceAsync(string keyspace, ReplicationOptions? options = null);
    Task ChangeKeyspaceAsync(string keyspace);
}
public interface ILinqDatabaseProvider : IGeneralDatabaseProvider
{
    DbContext Context { get; }
    Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(Expression<Func<TVaultModel, bool>> filter) where TVaultModel : class, IVaultModel;
    Task<TVaultModel?> QuerySingleAsync<TVaultModel>(Expression<Func<TVaultModel, bool>> filter) where TVaultModel : class, IVaultModel;
    Task<int> UpdateAsync<TVaultModel>(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls) where TVaultModel : class, IVaultModel;
    Task<int> DeleteAsync<TVaultModel>(Expression<Func<TVaultModel, bool>> filter) where TVaultModel : class, IVaultModel;
    Task<int> DeleteSingleAsync<TVaultModel>(TVaultModel model) where TVaultModel : class, IVaultModel;
    Task<int> DeleteMultipleAsync<TVaultModel>(TVaultModel model) where TVaultModel : class, IVaultModel;
}

public interface ICqlDatabaseProvider : IGeneralDatabaseProvider
{
    Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(string cqlQuery, List<object> parameters) where TVaultModel : class, IVaultModel;
    Task<TVaultModel?> QuerySingleAsync<TVaultModel>(string cqlQuery, List<object> parameters) where TVaultModel : class, IVaultModel;
    Task<int> ExecuteAsync(string cqlQuery, List<object> parameters);
    Task<int> UpdateAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel;
    Task<int> DeleteAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel;
}


public interface ITypeErasedVault
{
    Task SaveAsync(object entity, bool? saveHistory = false);
    Task SaveBatchAsync(IEnumerable<object> entities, bool? saveHistory = false);
}

public interface IVault<TVaultModel> : ITypeErasedVault where TVaultModel : class, IVaultModel
{
    IKeyspace Keyspace { get; }
    IVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate);
    IVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);
    IVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);
    IVault<TVaultModel> Take(int count);
    Task<List<TVaultModel>> ToListAsync();
    Task<ICursor<TVaultModel>> ToCursorAsync();
    Task<TVaultModel?> FirstOrDefaultAsync();
    Task<TVaultModel?> FirstAsync();
    Task<List<TVaultModel>> ToListAsync(Expression<Func<TVaultModel, bool>> predicate);
    Task<int> CountAsync();
    Task SaveAsync(TVaultModel entity, bool? saveHistory = false);
    Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false);
    Task<int> UpdateAsync(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls);
    Task<bool> DeleteAsync();
    Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate);
    IVault<TVaultModel> Skip(int count);
    Task<IEnumerable<TResult>> SelectAsync<TResult>(Expression<Func<TVaultModel, TResult>> selector) where TResult : class, IVaultModel;

}

public interface ICqlVault<TVaultModel> : IVault<TVaultModel> where TVaultModel : class, IVaultModel
{

}