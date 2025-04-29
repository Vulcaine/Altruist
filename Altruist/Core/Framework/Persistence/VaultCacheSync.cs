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

namespace Altruist.Database;

public interface IVaultCacheSyncService<TVaultModel> where TVaultModel : class, IVaultModel
{
    public Task Sync();

    public Task SaveAsync(TVaultModel entity, string cacheGroupId = "");

    public Task<TVaultModel?> DeleteAsync(string id, string cacheGroupId = "");

    public Task<TVaultModel?> FindCachedByIdAsync(string id, string cacheGroupId = "");

    public Task<ICursor<TVaultModel>> FindAllCachedAsync(string cacheGroupId = "");

    public Task<TVaultModel?> FindPersistedByIdAsync(string id);

    public Task<ICursor<TVaultModel>> FindAllPersistedAsync();

    public IVault<TVaultModel> VaultModel();
}

public abstract class AbstractVaultCacheSyncService<TVaultModel> : IVaultCacheSyncService<TVaultModel> where TVaultModel : class, IVaultModel
{
    protected readonly ICacheProvider _cacheProvider;
    protected readonly IVault<TVaultModel>? _vault;

    public AbstractVaultCacheSyncService(ICacheProvider cacheProvider, IVault<TVaultModel>? vault = null)
    {
        _cacheProvider = cacheProvider;
        _vault = vault;
    }

    protected void ValidateVault()
    {
        if (_vault == null)
        {
            throw new InvalidOperationException($"Vault for type {typeof(TVaultModel).Name} is not registered. Please make sure you set the database configuration correctly and register the vault.");
        }
    }

    public Task<ICursor<TVaultModel>> FindAllCachedAsync(string cacheGroupId = "")
    {
        return _cacheProvider.GetAllAsync<TVaultModel>(cacheGroupId);
    }

    public Task<ICursor<TVaultModel>> FindAllPersistedAsync()
    {
        ValidateVault();
        return _vault!.ToCursorAsync();
    }

    public Task<TVaultModel?> FindCachedByIdAsync(string id, string cacheGroupId = "")
    {
        return _cacheProvider.GetAsync<TVaultModel>(id, cacheGroupId);
    }

    public Task<TVaultModel?> FindPersistedByIdAsync(string id)
    {
        ValidateVault();
        return _vault!.Where(x => x.SysId == id).FirstOrDefaultAsync();
    }

    public virtual async Task Sync()
    {
        ValidateVault();
        var all = await _vault!.ToListAsync();
        List<Task> tasks = new();
        foreach (var entity in all)
        {
            tasks.Add(_cacheProvider.SaveAsync(entity.SysId, entity));
        }

        Task.WaitAll(tasks);
    }

    public async Task SaveAsync(TVaultModel entity, string cacheGroupId = "")
    {
        await _cacheProvider.SaveAsync(entity.SysId, entity, cacheGroupId);

        if (_vault != null)
        {
            await _vault.SaveAsync(entity);
        }

    }

    public IVault<TVaultModel> VaultModel()
    {
        ValidateVault();
        return _vault!;
    }

    public async Task<TVaultModel?> DeleteAsync(string id, string cacheGroupId = "")
    {
        if (_vault == null)
        {
            return await _cacheProvider.RemoveAsync<TVaultModel>(id, cacheGroupId);
        }
        else
        {
            var deletedFromVault = await _vault.Where(x => x.SysId == id).DeleteAsync();
            if (deletedFromVault)
            {
                return await _cacheProvider.RemoveAsync<TVaultModel>(id, cacheGroupId);
            }
        }

        return null;
    }
}