using System.Linq.Expressions;
using Altruist.ScyllaDB;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Altruist.EFCore;

public class EfCoreDatabaseProvider : ILinqDatabaseProvider
{
    public DbContext Context { get; }

    public EfCoreDatabaseProvider(DbContext context)
    {
        Context = context;
    }

    public async Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(Expression<Func<TVaultModel, bool>> filter) where TVaultModel : class, IVaultModel
    {
        return await Context.Set<TVaultModel>().Where(filter).ToListAsync();
    }

    public async Task<int> UpdateAsync<TVaultModel>(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls) where TVaultModel : class, IVaultModel
    {
        var entitiesToUpdate = Context.Set<TVaultModel>();

        return await entitiesToUpdate
            .ExecuteUpdateAsync(setPropertyCalls);
    }

    Task IGeneralDatabaseProvider.CreateTableAsync<TVaultModel>(IKeyspace? keyspace = null)
    {
        throw new NotImplementedException();
    }

    public Task CreateTableAsync(Type vaultModel, IKeyspace? keyspace = null)
    {
        throw new NotImplementedException();
    }

    public Task CreateKeySpaceAsync(string keyspace, ReplicationOptions? options = null)
    {
        throw new NotImplementedException();
    }

    public Task ChangeKeyspaceAsync(string keyspace)
    {
        throw new NotImplementedException();
    }
}

