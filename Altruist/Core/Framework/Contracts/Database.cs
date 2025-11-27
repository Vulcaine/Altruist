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

using Altruist.Contracts;
using Altruist.Persistence;
using Altruist.UORM;

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
    public string StorageId { get; set; }
}

public abstract class StoredModel : IStoredModel
{
    public abstract string StorageId { get; set; }
    public abstract string Type { get; set; }

    // public virtual string Key { get; set; } = "";

    // public virtual string Group { get; set; } = "";

    // [JsonIgnore]
    // public string StoredId => $"{Group}:{Key}";
}

public interface IVaultFactory<TToken, TConfig> where TConfig : IAltruistConfiguration where TToken : IServiceToken<TConfig>
{
    public TToken Token { get; }
}

public interface IPrefabModel : IVaultModel
{
    // JSONB mapping: component name → StorageId
    Dictionary<string, string?> ComponentRefs { get; set; }
}

public interface IVaultModel : IStoredModel, IVaultOnSave
{
    DateTime Timestamp { get; set; }
}

public interface IVaultOnSave
{
    void OnSave();
}

[VaultPrimaryKey(nameof(StorageId))]
public abstract class VaultModel : StoredModel, IVaultModel
{
    [VaultColumn("created-at")]
    public virtual DateTime Timestamp { get; set; } = default!;

    [VaultColumn("id")]
    public override string StorageId { get; set; } = default!;

    [VaultColumn("type")]
    public override string Type { get; set; } = default!;

    public void OnSave()
    {
        StorageId = this is IIdGenerator idGenerator ? idGenerator.GenerateId() : (string.IsNullOrEmpty(StorageId) ? Guid.NewGuid().ToString() : StorageId);
        Timestamp = DateTime.UtcNow;
        Type = GetType().Name;
    }
}

public interface ILinqVault<TVaultModel> : IVault<TVaultModel> where TVaultModel : class, IVaultModel
{

}

public interface IVaultRepository<TKeyspace> where TKeyspace : class, IKeyspace
{
    IDatabaseServiceToken Token { get; }
    IVault<TVaultModel> Select<TVaultModel>() where TVaultModel : class, IVaultModel;
}

public interface IGeneralDatabaseProvider : IConnectable
{
    IDatabaseServiceToken Token { get; }
    string GetConnectionString();
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

public interface IVault<TVaultModel> where TVaultModel : class, IVaultModel
{
    IKeyspace Keyspace { get; }
    IHistoricalVault<TVaultModel> History { get; }
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
    Task<long> CountAsync();
    Task<bool> DeleteAsync();
    Task<bool> AnyAsync(Expression<Func<TVaultModel, bool>> predicate);
    IVault<TVaultModel> Skip(int count);
    Task<IEnumerable<TResult>> SelectAsync<TResult>(Expression<Func<TVaultModel, TResult>> selector) where TResult : class, IVaultModel;

}

public interface IHistoricalVault<TVaultModel> where TVaultModel : class, IVaultModel
{
    // Fluent filters – same spirit as IVault, but scoped to history
    IHistoricalVault<TVaultModel> Where(Expression<Func<TVaultModel, bool>> predicate);
    IHistoricalVault<TVaultModel> OrderBy<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);
    IHistoricalVault<TVaultModel> OrderByDescending<TKey>(Expression<Func<TVaultModel, TKey>> keySelector);
    IHistoricalVault<TVaultModel> Take(int count);
    IHistoricalVault<TVaultModel> Skip(int count);

    // Historical range query (inclusive)
    Task<List<TVaultModel>> ToListAsync(DateTime startTime, DateTime endTime);
}

public interface ICqlVault<TVaultModel> : IVault<TVaultModel> where TVaultModel : class, IVaultModel
{

}

/// <summary>
/// Resolve typed repositories by compile-time keyspace type or at runtime by keyspace name.
/// </summary>
public interface IRepositoryFactory
{
    /// <summary>
    /// Return a strongly-typed repository for the given keyspace type.
    /// </summary>
    IVaultRepository<TKeyspace> Make<TKeyspace>() where TKeyspace : class, IKeyspace;

    /// <summary>
    /// Return a repository for the keyspace instance whose <see cref="IKeyspace.Name"/> matches.
    /// Since the keyspace type is only known at runtime, this returns a non-generic
    /// adapter that still supports <c>Select&lt;TModel&gt;()</c> and <c>Select(Type)</c>.
    /// </summary>
    IAnyVaultRepository Make(string keyspaceName);
}

/// <summary>
/// Non-generic repository surface so callers that only have a keyspace name can still call
/// <c>Select&lt;TModel&gt;()</c> and <c>Select(Type)</c>.
/// </summary>
public interface IAnyVaultRepository
{
    IDatabaseServiceToken Token { get; }
    IKeyspace Keyspace { get; }

    IVault<TVaultModel> Select<TVaultModel>() where TVaultModel : class, IVaultModel;
}
