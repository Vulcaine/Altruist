namespace Altruist;

public interface ICursor<T> where T : notnull
{
    List<T> Items { get; }
    bool HasNext { get; }
    Task<bool> NextBatch();
    IEnumerator<T> GetEnumerator();
}


public interface ICacheProvider
{

    Task<bool> ContainsAsync<T>(string key) where T : notnull;
    Task<T?> GetAsync<T>(string key) where T : notnull;
    Task<ICursor<T>> GetAllAsync<T>() where T : notnull;
    Task<ICursor<object>> GetAllAsync(Type type);
    Task SaveAsync<T>(string key, T entity) where T : notnull;
    Task SaveBatchAsync<T>(Dictionary<string, T> entities) where T : notnull;
    Task<T?> RemoveAsync<T>(string key) where T : notnull;
    Task ClearAsync<T>() where T : notnull;
    Task ClearAllAsync();
    // Task<List<string>> GetBatchKeysAsync(string baseKey, int skip, int take);
}

public interface IExternalCacheProvider : ICacheProvider
{
}


public interface IMemoryCacheProvider : ICacheProvider
{
}
