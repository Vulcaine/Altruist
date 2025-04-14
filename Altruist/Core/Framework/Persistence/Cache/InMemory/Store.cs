
using System.Collections.Concurrent;
using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.InMemory;

public sealed class InMemoryServiceConfiguration : ICacheConfiguration
{
    public void Configure(IServiceCollection services)
    {

    }
}

public sealed class InMemoryCacheServiceToken : ICacheServiceToken
{
    public static readonly InMemoryCacheServiceToken Instance = new();
    public ICacheConfiguration Configuration { get; }

    public InMemoryCacheServiceToken()
    {
        Configuration = new InMemoryServiceConfiguration();
    }

    public string Description => "💾 Cache: InMemory";
}


public class InMemoryCache : IMemoryCacheProvider
{
    private readonly ConcurrentDictionary<Type, Dictionary<string, object>> _memoryCacheEntities = new();

    public event Action? OnConnected;
    public event Action<Exception> OnRetryExhausted = delegate { };

    public ICacheServiceToken Token => new InMemoryCacheServiceToken();

    public bool IsConnected => true;

    public Task<ICursor<T>> GetAllAsync<T>() where T : notnull
    {
        return CreateCursorAsync<T>(int.MaxValue);
    }

    public Task<ICursor<object>> GetAllAsync(Type type)
    {
        return CreateCursorAsync<object>(int.MaxValue);
    }

    private Task<ICursor<T>> CreateCursorAsync<T>(int batchSize) where T : notnull
    {
        var cacheMap = GetOrCreateCacheMap(typeof(T));
        var cursor = new InMemoryCacheCursor<T>(cacheMap, batchSize);
        return Task.FromResult(cursor as ICursor<T>);
    }

    public Task<T?> GetAsync<T>(string key) where T : notnull
    {
        var cacheMap = GetOrCreateCacheMap(typeof(T));

        if (cacheMap == null || !cacheMap.ContainsKey(key))
        {
            return Task.FromResult(default(T));
        }

        var entity = cacheMap[key];
        return Task.FromResult((T)entity)!;
    }


    public Task<List<string>> GetBatchKeysAsync(string baseKey, int skip, int take)
    {
        var keys = _memoryCacheEntities.Values
            .SelectMany(cacheMap => cacheMap.Keys)
            .Where(key => key.StartsWith(baseKey))
            .Skip(skip)
            .Take(take)
            .ToList();

        return Task.FromResult(keys);
    }

    public Task<T?> RemoveAsync<T>(string key) where T : notnull
    {
        var cacheMap = GetOrCreateCacheMap(typeof(T));
        if (cacheMap == null || !cacheMap.ContainsKey(key))
        {
            return Task.FromResult(default(T));
        }

        var old = cacheMap[key];
        cacheMap.Remove(key);
        return Task.FromResult((T)old)!;
    }

    public Task SaveAsync<T>(string key, T entity) where T : notnull
    {
        var cacheMap = GetOrCreateCacheMap(typeof(T));
        if (cacheMap == null)
        {
            return Task.CompletedTask;
        }
        cacheMap[key] = entity!;
        return Task.CompletedTask;
    }

    public Task SaveBatchAsync<T>(Dictionary<string, T> entities) where T : notnull
    {
        foreach (var entity in entities)
        {
            var cacheMap = GetOrCreateCacheMap(typeof(T));
            cacheMap[entity.Key] = entity.Value!;
        }

        return Task.CompletedTask;
    }

    private Dictionary<string, object> GetOrCreateCacheMap(Type type)
    {
        if (!_memoryCacheEntities.ContainsKey(type))
        {
            _memoryCacheEntities[type] = new();
        }

        return _memoryCacheEntities[type];
    }

    public Task ClearAsync<T>() where T : notnull
    {
        _memoryCacheEntities[typeof(T)].Clear();
        return Task.CompletedTask;
    }

    public Task ClearAllAsync()
    {
        _memoryCacheEntities.Clear();
        return Task.CompletedTask;
    }

    public Task<bool> ContainsAsync<T>(string key) where T : notnull
    {
        var cacheMap = GetOrCreateCacheMap(typeof(T));
        return Task.FromResult(cacheMap.ContainsKey(key));
    }

    public Task RemoveAndForgetAsync<T>(string key) where T : notnull
    {
        var cacheMap = GetOrCreateCacheMap(typeof(T));
        cacheMap.Remove(key);
        return Task.CompletedTask;
    }

    public Task ConnectAsync(int maxRetries = 30, int delayMilliseconds = 2000)
    {
        OnConnected?.Invoke();
        return Task.CompletedTask;
    }
}


public class InMemoryConnectionStore : AbstractConnectionStore
{
    public InMemoryConnectionStore(IMemoryCacheProvider memoryCache, ILoggerFactory loggerFactory) : base(memoryCache, loggerFactory)
    {
    }
}
