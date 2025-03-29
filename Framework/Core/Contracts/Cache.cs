namespace Altruist;

public interface ICache
{
    Task<T?> GetAsync<T>(string key);
    Task<CacheCursor<T>> GetAllAsync<T>();
    Task<CacheCursor<object>> GetAllAsync(Type type);
    Task SaveAsync<T>(string key, T entity);
    Task SaveBatchAsync<T>(Dictionary<string, T> entities);
    Task<bool> RemoveAsync<T>(string key);
    Task ClearAsync();

    Task<List<string>> GetBatchKeysAsync(string baseKey, int skip, int take);
}

public interface IExternalCache : ICache
{
}


public interface IMemoryCache : ICache
{
}
