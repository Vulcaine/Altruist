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

using System.Collections;
using System.Collections.Concurrent;
using Altruist.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Altruist.InMemory;

using GroupCache = ConcurrentDictionary<string, EfficientConcurrentCache<object>>;

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
public class EfficientConcurrentCache<T> : IEnumerable<T>
{
    private readonly List<T> _items = new();
    private readonly Dictionary<string, int> _indexMap = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

    private List<T>? _lastSnapshot;
    private bool _updated = true;

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _items.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public IEnumerable<string> Keys
    {
        get
        {
            _lock.EnterReadLock();
            try
            {
                return _indexMap.Keys;
            }
            finally { _lock.ExitReadLock(); }
        }
    }

    public void Add(string key, T value)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_indexMap.TryGetValue(key, out var index))
                _items[index] = value;
            else
            {
                _indexMap[key] = _items.Count;
                _items.Add(value);
            }
            _updated = true;
        }
        finally { _lock.ExitWriteLock(); }
    }

    public bool TryGet(string key, out T? value)
    {
        _lock.EnterReadLock();
        try
        {
            if (_indexMap.TryGetValue(key, out var index))
            {
                value = _items[index];
                return true;
            }
            value = default;
            return false;
        }
        finally { _lock.ExitReadLock(); }
    }

    public void Remove(string key)
    {
        _lock.EnterWriteLock();
        try
        {
            if (_indexMap.TryGetValue(key, out int index))
            {
                int last = _items.Count - 1;
                if (index != last)
                {
                    _items[index] = _items[last];
                    var lastKey = _indexMap.First(kv => kv.Value == last).Key;
                    _indexMap[lastKey] = index;
                }

                _items.RemoveAt(last);
                _indexMap.Remove(key);
                _updated = true;
            }
        }
        finally { _lock.ExitWriteLock(); }
    }

    public IEnumerator<T> GetEnumerator()
    {
        List<T> snapshot;

        _lock.EnterUpgradeableReadLock();
        try
        {
            if (_updated || _lastSnapshot == null)
            {
                _lock.EnterWriteLock();
                try
                {
                    _lastSnapshot = new List<T>(_items);
                    _updated = false;
                }
                finally { _lock.ExitWriteLock(); }
            }

            snapshot = _lastSnapshot!;
        }
        finally { _lock.ExitUpgradeableReadLock(); }

        return snapshot.GetEnumerator();
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}


public class InMemoryCache : IMemoryCacheProvider
{
    private readonly ConcurrentDictionary<Type, GroupCache> _cacheSource = new();
    private readonly ConcurrentDictionary<Type, ICursorToken> _cursorPerSource = new();
    public ICacheServiceToken Token => new InMemoryCacheServiceToken();

    public IEnumerable<Type> AvailableTypes => _cacheSource.Keys;

    private EfficientConcurrentCache<object> GetOrCreateEntityCache(Type type, string cacheGroupId = "")
    {
        var groupMap = _cacheSource.GetOrAdd(type, _ => new GroupCache());
        return groupMap.GetOrAdd(cacheGroupId ?? "", _ => new EfficientConcurrentCache<object>());
    }

    public Task<T?> GetAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        return Task.FromResult(map.TryGet(key, out var value) ? (T)value! : default);
    }

    public Task<ICursor<T>> GetAllAsync<T>(string cacheGroupId = "") where T : notnull
    {
        var source = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        var cursor = new InMemoryCacheCursor<T>(source, int.MaxValue);
        _cursorPerSource[typeof(T)] = cursor;
        return Task.FromResult(cursor as ICursor<T>);
    }

    public Task<ICursor<object>> GetAllAsync(Type type, string cacheGroupId = "")
    {
        var source = GetOrCreateEntityCache(type, cacheGroupId);
        var cursor = new InMemoryCacheCursor<object>(source, int.MaxValue);
        _cursorPerSource[type] = cursor;
        return Task.FromResult(cursor as ICursor<object>);
    }

    public Task SaveAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        map.Add(key, entity!);
        return Task.CompletedTask;
    }

    public Task SaveBatchAsync<T>(Dictionary<string, T> entities, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        foreach (var kv in entities)
        {
            map.Add(kv.Key, kv.Value!);
        }
        return Task.CompletedTask;
    }

    public Task<T?> RemoveAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        if (map.TryGet(key, out var value))
        {
            map.Remove(key);
            return Task.FromResult((T?)value);
        }

        return Task.FromResult<T?>(default);
    }

    public Task RemoveAndForgetAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        map.Remove(key);
        return Task.CompletedTask;
    }

    public Task<bool> ContainsAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        return Task.FromResult(map.TryGet(key, out _));
    }

    public Task ClearAsync<T>(string cacheGroupId = "") where T : notnull
    {
        var groupMap = _cacheSource.GetValueOrDefault(typeof(T));
        groupMap?.TryRemove(cacheGroupId ?? "", out _);
        return Task.CompletedTask;
    }

    public Task ClearAllAsync()
    {
        _cacheSource.Clear();
        return Task.CompletedTask;
    }

    public Task<List<string>> GetBatchKeysAsync(string baseKey, int skip, int take, string cacheGroupId = "")
    {
        var result = new List<string>(take);
        int skipped = 0;

        foreach (var group in _cacheSource.Values)
        {
            if (!group.TryGetValue(cacheGroupId ?? "", out var map))
                continue;

            foreach (var key in map.Keys)
            {
                if (!key.StartsWith(baseKey))
                    continue;

                if (skipped < skip)
                {
                    skipped++;
                    continue;
                }

                result.Add(key);

                if (result.Count >= take)
                    return Task.FromResult(result);
            }
        }

        return Task.FromResult(result);
    }
}



public class InMemoryConnectionStore : AbstractConnectionStore
{
    public InMemoryConnectionStore(IMemoryCacheProvider memoryCache, ILoggerFactory loggerFactory) : base(memoryCache, loggerFactory)
    {
    }
}
