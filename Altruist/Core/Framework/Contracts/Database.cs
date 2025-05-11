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

using System.Linq.Expressions;
using System.Text.Json.Serialization;
using Altruist.Contracts;
using Altruist.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Altruist;

public interface IKeyspace
{
    IDatabaseServiceToken DatabaseToken { get; }
    string Name { get; }
}

public interface IIdGenerator
{
    public string GenerateId();
}

public interface ITypedModel
{
    public string Type { get; set; }
}

public interface IStoredModel : ITypedModel
{
    public string SysId { get; set; }
    public string GroupId { get; set; }
}

public abstract class StoredModel : IStoredModel
{
    public abstract string SysId { get; set; }
    public virtual string GroupId { get; set; } = "";
    public abstract string Type { get; set; }

    public virtual string Key { get; set; }

    public virtual string Group { get; set; }

    [JsonIgnore]
    public string StoredId => $"{Group}:{Key}";
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

public interface IVaultModel : IStoredModel
{
    DateTime Timestamp { get; set; }
}

public abstract class VaultModel : StoredModel, IVaultModel
{
    public abstract DateTime Timestamp { get; set; }

    public VaultModel()
    {
        SysId = this is IIdGenerator idGenerator ? idGenerator.GenerateId() : (string.IsNullOrEmpty(SysId) ? Guid.NewGuid().ToString() : SysId);
    }
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

public interface IGeneralDatabaseProvider : IConnectable
{
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
    Task<IEnumerable<TVaultModel>> QueryAsync<TVaultModel>(string cqlQuery, List<object>? parameters = null) where TVaultModel : class, IVaultModel;
    Task<TVaultModel?> QuerySingleAsync<TVaultModel>(string cqlQuery, List<object>? parameters = null) where TVaultModel : class, IVaultModel;
    Task<long> ExecuteAsync(string cqlQuery, List<object>? parameters = null);
    Task<long> UpdateAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel;
    Task<long> DeleteAsync<TVaultModel>(TVaultModel entity) where TVaultModel : class, IVaultModel;
    Task<long> ExecuteCountAsync(string cqlQuery, List<object>? parameters = null);
}


public interface ITypeErasedVault
{
    Task SaveAsync(object entity, bool? saveHistory = false);
    Task SaveBatchAsync(IEnumerable<object> entities, bool? saveHistory = false);
    Task<long> CountAsync();
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
    Task SaveAsync(TVaultModel entity, bool? saveHistory = false);
    Task SaveBatchAsync(IEnumerable<TVaultModel> entities, bool? saveHistory = false);
    Task<long> UpdateAsync(Expression<Func<SetPropertyCalls<TVaultModel>, SetPropertyCalls<TVaultModel>>> setPropertyCalls);
    Task<bool> DeleteAsync();
    Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate);
    IVault<TVaultModel> Skip(int count);
    Task<IEnumerable<TResult>> SelectAsync<TResult>(Expression<Func<TVaultModel, TResult>> selector) where TResult : class, IVaultModel;

}

public interface ICqlVault<TVaultModel> : IVault<TVaultModel> where TVaultModel : class, IVaultModel
{

}