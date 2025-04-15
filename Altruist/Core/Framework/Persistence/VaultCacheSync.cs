namespace Altruist.Database;

public interface IVaultCacheSyncService<TVaultModel> where TVaultModel : class, IVaultModel
{
    public Task Load();

    public Task SaveAsync(TVaultModel entity);

    public Task<bool> DeleteAsync(string id);

    public Task<TVaultModel?> FindCachedByIdAsync(string id);

    public Task<ICursor<TVaultModel>> FindAllCachedAsync();

    public Task<TVaultModel?> FindPersistedByIdAsync(string id);

    public Task<ICursor<TVaultModel>> FindAllPersistedAsync();

    public IVault<TVaultModel> VaultModel();
}

public abstract class AbstractVaultCacheSyncService<TVaultModel> : IVaultCacheSyncService<TVaultModel> where TVaultModel : class, IVaultModel
{
    private readonly ICacheProvider _cacheProvider;
    private readonly IVault<TVaultModel>? _vault;

    public AbstractVaultCacheSyncService(ICacheProvider cacheProvider, IVault<TVaultModel>? vault = null)
    {
        _cacheProvider = cacheProvider;
        _vault = vault;
    }

    public void ValidateVault()
    {
        if (_vault == null)
        {
            throw new InvalidOperationException($"Vault for type {typeof(TVaultModel).Name} is not registered. Please make sure you set the database configuration correctly and register the vault.");
        }
    }

    public Task<ICursor<TVaultModel>> FindAllCachedAsync()
    {
        return _cacheProvider.GetAllAsync<TVaultModel>();
    }

    public Task<ICursor<TVaultModel>> FindAllPersistedAsync()
    {
        ValidateVault();
        return _vault!.ToCursorAsync();
    }

    public Task<TVaultModel?> FindCachedByIdAsync(string id)
    {
        return _cacheProvider.GetAsync<TVaultModel>(id);
    }

    public Task<TVaultModel?> FindPersistedByIdAsync(string id)
    {
        ValidateVault();
        return _vault!.Where(x => x.GenId == id).FirstOrDefaultAsync();
    }

    public virtual async Task Load()
    {
        ValidateVault();
        var all = await _vault!.ToListAsync();
        List<Task> tasks = new();
        foreach (var entity in all)
        {
            tasks.Add(_cacheProvider.SaveAsync<TVaultModel>(entity.GenId, entity));
        }

        Task.WaitAll(tasks);
    }

    public async Task SaveAsync(TVaultModel entity)
    {
        await _cacheProvider.SaveAsync(entity.GenId, entity);

        if (_vault != null)
        {
            _ = _vault.SaveAsync(entity);
        }

    }

    public IVault<TVaultModel> VaultModel()
    {
        ValidateVault();
        return _vault!;
    }

    public async Task<bool> DeleteAsync(string id)
    {
        ValidateVault();
        var deletedFromVault = await _vault!.Where(x => x.GenId == id).DeleteAsync();
        if (deletedFromVault || _vault == null)
        {
            await _cacheProvider.RemoveAsync<TVaultModel>(id);
            return true;
        }

        return false;
    }
}