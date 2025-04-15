
using System.Collections.Concurrent;
using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.InMemory;

using GroupCache = ConcurrentDictionary<string, ConcurrentDictionary<string, object>>;
using EntityCache = ConcurrentDictionary<string, object>;

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
    private readonly ConcurrentDictionary<Type, GroupCache> _cache = new();
    public ICacheServiceToken Token => new InMemoryCacheServiceToken();

    private EntityCache GetOrCreateEntityCache(Type type, string group = "")
    {
        var groupMap = _cache.GetOrAdd(type, _ => new GroupCache());
        return groupMap.GetOrAdd(group ?? "", _ => new EntityCache());
    }

    public Task<T?> GetAsync<T>(string key, string group = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), group);
        return Task.FromResult(map.TryGetValue(key, out var value) ? (T)value : default);
    }

    public Task<ICursor<T>> GetAllAsync<T>(string group = "") where T : notnull
    {
        Dictionary<string, object> dict;

        if (group == "")
        {
            if (_cache.TryGetValue(typeof(T), out var allGroups))
            {
                dict = allGroups.Values
                    .SelectMany(d => d)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }
            else
            {
                dict = new Dictionary<string, object>();
            }
        }
        else
        {
            dict = GetOrCreateEntityCache(typeof(T), group).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        return Task.FromResult(new InMemoryCacheCursor<T>(dict, int.MaxValue) as ICursor<T>);
    }

    public Task<ICursor<object>> GetAllAsync(Type type, string group = "")
    {
        Dictionary<string, object> dict;

        if (group == "")
        {
            if (_cache.TryGetValue(type, out var allGroups))
            {
                dict = allGroups.Values
                    .SelectMany(d => d)
                    .ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
            }
            else
            {
                dict = new Dictionary<string, object>();
            }
        }
        else
        {
            dict = GetOrCreateEntityCache(type, group).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        return Task.FromResult(new InMemoryCacheCursor<object>(dict, int.MaxValue) as ICursor<object>);
    }


    public Task SaveAsync<T>(string key, T entity, string group = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), group);
        map[key] = entity!;
        return Task.CompletedTask;
    }

    public Task SaveBatchAsync<T>(Dictionary<string, T> entities, string group = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), group);
        foreach (var kv in entities)
        {
            map[kv.Key] = kv.Value!;
        }
        return Task.CompletedTask;
    }

    public Task<T?> RemoveAsync<T>(string key, string group = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), group);
        return Task.FromResult(map.TryRemove(key, out var value) ? (T)value : default);
    }

    public Task RemoveAndForgetAsync<T>(string key, string group = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), group);
        map.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ContainsAsync<T>(string key, string group = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), group);
        return Task.FromResult(map.ContainsKey(key));
    }

    public Task ClearAsync<T>(string group = "") where T : notnull
    {
        if (_cache.TryGetValue(typeof(T), out var groupMap))
        {
            if (groupMap.TryGetValue(group ?? "", out var map))
            {
                map.Clear();
            }
        }
        return Task.CompletedTask;
    }

    public Task ClearAllAsync()
    {
        _cache.Clear();
        return Task.CompletedTask;
    }

    public Task<List<string>> GetBatchKeysAsync(string baseKey, int skip, int take, string group = "")
    {
        var keys = _cache.Values
            .SelectMany(g => g.Values)
            .SelectMany(d => d.Keys)
            .Where(k => k.StartsWith(baseKey))
            .Skip(skip)
            .Take(take)
            .ToList();

        return Task.FromResult(keys);
    }
}



public class InMemoryConnectionStore : AbstractConnectionStore
{
    public InMemoryConnectionStore(IMemoryCacheProvider memoryCache, ILoggerFactory loggerFactory) : base(memoryCache, loggerFactory)
    {
    }
}
