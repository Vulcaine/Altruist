
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Altruist.InMemory;

public sealed class CacheMap
{
    public Type ForType { get; }
    private readonly ConcurrentDictionary<string, List<object>> _cache;

    public CacheMap(Type forType)
    {
        ForType = forType;
        _cache = new ConcurrentDictionary<string, List<object>>();
    }

    public void SaveEntity<T>(string key, T entity)
    {
        if (!_cache.ContainsKey(key))
        {
            _cache[key] = new List<object>();
        }

        _cache[key].Add(entity!);
    }

    public List<T> GetAllEntities<T>()
    {
        return _cache.Values
            .SelectMany(list => list)
            .OfType<T>()
            .ToList();
    }

    public List<T>? GetEntities<T>(string key)
    {
        if (_cache.TryGetValue(key, out var list))
        {
            return list.OfType<T>().ToList();
        }
        return null;
    }

    public bool TryRemove(string key, out List<object>? removedItems)
    {
        return _cache.TryRemove(key, out removedItems);
    }

    public IEnumerable<string> Keys => _cache.Keys;
}


public class InMemoryCache : IMemoryCache
{
    private readonly ConcurrentDictionary<Type, CacheMap> _memoryCacheEntities = new();

    public Task ClearAsync()
    {
        _memoryCacheEntities.Clear();
        return Task.CompletedTask;
    }

    public Task<CacheCursor<T>> GetAllAsync<T>()
    {
        return CreateCursorAsync<T>(string.Empty, int.MaxValue);
    }

    public Task<CacheCursor<object>> GetAllAsync(Type type)
    {
        return CreateCursorAsync<object>(string.Empty, int.MaxValue);
    }

    private Task<CacheCursor<T>> CreateCursorAsync<T>(string baseKey, int batchSize)
    {
        var cursor = new CacheCursor<T>(this, baseKey, batchSize);
        return Task.FromResult(cursor);
    }

    public Task<T?> GetAsync<T>(string key)
    {
        var cacheMap = GetOrCreateCacheMap(typeof(T));
        var entities = cacheMap.GetEntities<T>(key);
        if (entities == null)
        {
            return null!;
        }
        return Task.FromResult(entities!.FirstOrDefault());
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

    public Task<bool> RemoveAsync<T>(string key)
    {
        var cacheMap = GetOrCreateCacheMap(typeof(T));
        return Task.FromResult(cacheMap.TryRemove(key, out _));
    }

    public Task SaveAsync<T>(string key, T entity)
    {
        var cacheMap = GetOrCreateCacheMap(typeof(T));
        cacheMap.SaveEntity(key, entity);
        return Task.CompletedTask;
    }

    public Task SaveBatchAsync<T>(Dictionary<string, T> entities)
    {
        foreach (var entity in entities)
        {
            var cacheMap = GetOrCreateCacheMap(typeof(T));
            cacheMap.SaveEntity(entity.Key, entity.Value);
        }

        return Task.CompletedTask;
    }

    private CacheMap GetOrCreateCacheMap(Type type)
    {
        if (!_memoryCacheEntities.ContainsKey(type))
        {
            _memoryCacheEntities[type] = new CacheMap(type);
        }

        return _memoryCacheEntities[type];
    }
}


public class InMemoryConnectionStore : AbstractConnectionStore
{
    public InMemoryConnectionStore(IMemoryCache memoryCache, ILoggerFactory loggerFactory) : base(memoryCache, loggerFactory)
    {
    }
}
