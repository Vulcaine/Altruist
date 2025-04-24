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

    private EntityCache GetOrCreateEntityCache(Type type, string cacheGroupId = "")
    {
        var groupMap = _cache.GetOrAdd(type, _ => new GroupCache());
        return groupMap.GetOrAdd(cacheGroupId ?? "", _ => new EntityCache());
    }

    public Task<T?> GetAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        return Task.FromResult(map.TryGetValue(key, out var value) ? (T)value : default);
    }

    public Task<ICursor<T>> GetAllAsync<T>(string cacheGroupId = "") where T : notnull
    {
        Dictionary<string, object> dict;

        if (cacheGroupId == "")
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
            dict = GetOrCreateEntityCache(typeof(T), cacheGroupId).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        return Task.FromResult(new InMemoryCacheCursor<T>(dict, int.MaxValue) as ICursor<T>);
    }

    public Task<ICursor<object>> GetAllAsync(Type type, string cacheGroupId = "")
    {
        Dictionary<string, object> dict;

        if (cacheGroupId == "")
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
            dict = GetOrCreateEntityCache(type, cacheGroupId).ToDictionary(kvp => kvp.Key, kvp => kvp.Value);
        }

        return Task.FromResult(new InMemoryCacheCursor<object>(dict, int.MaxValue) as ICursor<object>);
    }


    public Task SaveAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        map[key] = entity!;
        return Task.CompletedTask;
    }

    public Task SaveBatchAsync<T>(Dictionary<string, T> entities, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        foreach (var kv in entities)
        {
            map[kv.Key] = kv.Value!;
        }
        return Task.CompletedTask;
    }

    public Task<T?> RemoveAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        return Task.FromResult(map.TryRemove(key, out var value) ? (T)value : default);
    }

    public Task RemoveAndForgetAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        map.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<bool> ContainsAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        return Task.FromResult(map.ContainsKey(key));
    }

    public Task ClearAsync<T>(string cacheGroupId = "") where T : notnull
    {
        if (_cache.TryGetValue(typeof(T), out var groupMap))
        {
            if (groupMap.TryGetValue(cacheGroupId ?? "", out var map))
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

    public Task<List<string>> GetBatchKeysAsync(string baseKey, int skip, int take, string cacheGroupId = "")
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
