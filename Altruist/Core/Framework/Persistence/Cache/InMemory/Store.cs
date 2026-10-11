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

/// <summary>No-op cache configuration for the in-memory backend (nothing to connect or register).</summary>
public sealed class InMemoryServiceConfiguration : ICacheConfiguration
{
    /// <summary>Configuration flag set by the framework once configured.</summary>
    public bool IsConfigured { get; set; }

    /// <summary>Does nothing; the in-memory cache services are registered by their <c>[Service]</c> attributes.</summary>
    /// <param name="services">The service collection (unused).</param>
    /// <returns>A completed task.</returns>
    public Task Configure(IServiceCollection services)
    {
        return Task.CompletedTask;
    }
}

/// <summary>
/// Service token identifying the in-memory cache backend. Registered as <see cref="ICacheServiceToken"/> when
/// <c>altruist:persistence:cache:provider</c> is <c>inmemory</c>.
/// </summary>
[Service(typeof(ICacheServiceToken))]
[ConditionalOnConfig("altruist:persistence:cache:provider", havingValue: "inmemory")]
public sealed class InMemoryCacheServiceToken : ICacheServiceToken
{
    /// <summary>Shared instance.</summary>
    public static readonly InMemoryCacheServiceToken Instance = new();
    /// <summary>The (no-op) <see cref="InMemoryServiceConfiguration"/>.</summary>
    public ICacheConfiguration Configuration { get; }

    /// <summary>Creates the token with a fresh <see cref="InMemoryServiceConfiguration"/>.</summary>
    public InMemoryCacheServiceToken()
    {
        Configuration = new InMemoryServiceConfiguration();
    }

    /// <summary>Display text used in startup logs.</summary>
    public string Description => "💾 Cache: InMemory";
}
/// <summary>
/// Thread-safe string-keyed collection with list-backed storage, used as one (type, group) bucket of
/// <see cref="InMemoryCache"/>. Reads take a shared lock; enumeration returns a cached snapshot rebuilt after writes.
/// </summary>
/// <remarks>
/// <see cref="Add"/> and <see cref="TryGet"/> are O(1); <see cref="Remove"/> swaps with the last item and is O(n) in the
/// key count. Iteration order is not stable across removals.
/// </remarks>
/// <typeparam name="T">The stored value type.</typeparam>
public class EfficientConcurrentCache<T> : IEnumerable<T>
{
    private readonly List<T> _items = new();
    private readonly Dictionary<string, int> _indexMap = new();
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.SupportsRecursion);

    private List<T>? _lastSnapshot;
    private bool _updated = true;

    /// <summary>Number of entries.</summary>
    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try
            { return _items.Count; }
            finally { _lock.ExitReadLock(); }
        }
    }

    /// <summary>The keys of the entries.</summary>
    /// <remarks>Returns the live key collection (not a copy): enumerating it while another thread writes can throw.</remarks>
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

    /// <summary>Adds the value, or replaces the existing value for <paramref name="key"/>.</summary>
    /// <param name="key">Entry key.</param>
    /// <param name="value">Value to store.</param>
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

    /// <summary>Looks up a value by key.</summary>
    /// <param name="key">Entry key.</param>
    /// <param name="value">The value when found; default otherwise.</param>
    /// <returns>True when the key exists.</returns>
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

    /// <summary>Removes the entry for <paramref name="key"/> if present.</summary>
    /// <param name="key">Entry key.</param>
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

    /// <summary>Enumerates a point-in-time snapshot of the values; writes during enumeration do not affect it.</summary>
    /// <returns>Snapshot enumerator.</returns>
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

/// <summary>
/// Process-local cache: entries live in memory, keyed by (exact type <c>T</c>, cache group, key). Always registered as
/// the <see cref="IMemoryCacheProvider"/> singleton (connection stores use it); exposed as <see cref="ICacheProvider"/>
/// through <see cref="InMemoryCacheProviderAdapter"/> when <c>altruist:persistence:cache:provider</c> is <c>inmemory</c>.
/// </summary>
/// <remarks>
/// All operations complete synchronously (returned tasks are already completed) and are thread-safe. Values are
/// stored by reference, not copied: mutating a retrieved object mutates the cached entry. Nothing expires; remove
/// entries explicitly. Use <see cref="ICacheProvider"/> in application code so a Redis backend can be swapped in by config.
/// </remarks>
[Service(typeof(IMemoryCacheProvider))]
public class InMemoryCache : IMemoryCacheProvider
{
    private readonly ConcurrentDictionary<Type, GroupCache> _cacheSource = new();
    private readonly ConcurrentDictionary<Type, ICursorToken> _cursorPerSource = new();
    /// <summary>An <see cref="InMemoryCacheServiceToken"/> (a new instance per call).</summary>
    public ICacheServiceToken Token => new InMemoryCacheServiceToken();

    /// <summary>Types that have (or had) at least one bucket in the cache.</summary>
    public IEnumerable<Type> AvailableTypes => _cacheSource.Keys;

    private EfficientConcurrentCache<object> GetOrCreateEntityCache(Type type, string cacheGroupId = "")
    {
        var groupMap = _cacheSource.GetOrAdd(type, _ => new GroupCache());
        return groupMap.GetOrAdd(cacheGroupId ?? "", _ => new EfficientConcurrentCache<object>());
    }

    /// <inheritdoc/>
    public Task<T?> GetAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        return Task.FromResult(map.TryGet(key, out var value) ? (T)value! : default);
    }

    /// <inheritdoc/>
    /// <remarks>Returns an <see cref="InMemoryCacheCursor{T}"/>; enumerate it with <c>foreach</c> (it does not support <c>NextBatch</c>).</remarks>
    public Task<ICursor<T>> GetAllAsync<T>(string cacheGroupId = "") where T : notnull
    {
        var source = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        var cursor = new InMemoryCacheCursor<T>(source, int.MaxValue);
        _cursorPerSource[typeof(T)] = cursor;
        return Task.FromResult(cursor as ICursor<T>);
    }

    /// <inheritdoc/>
    public Task<ICursor<object>> GetAllAsync(Type type, string cacheGroupId = "")
    {
        var source = GetOrCreateEntityCache(type, cacheGroupId);
        var cursor = new InMemoryCacheCursor<object>(source, int.MaxValue);
        _cursorPerSource[type] = cursor;
        return Task.FromResult(cursor as ICursor<object>);
    }

    /// <inheritdoc/>
    public Task SaveAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        map.Add(key, entity!);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task SaveBatchAsync<T>(Dictionary<string, T> entities, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        foreach (var kv in entities)
        {
            map.Add(kv.Key, kv.Value!);
        }
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
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

    /// <inheritdoc/>
    public Task RemoveAndForgetAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        map.Remove(key);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<bool> ContainsAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var map = GetOrCreateEntityCache(typeof(T), cacheGroupId);
        return Task.FromResult(map.TryGet(key, out _));
    }

    /// <inheritdoc/>
    public Task ClearAsync<T>(string cacheGroupId = "") where T : notnull
    {
        var groupMap = _cacheSource.GetValueOrDefault(typeof(T));
        groupMap?.TryRemove(cacheGroupId ?? "", out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task ClearAllAsync()
    {
        _cacheSource.Clear();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns up to <paramref name="take"/> keys starting with <paramref name="baseKey"/> (ordinal, case-sensitive) in the
    /// given group, after skipping <paramref name="skip"/> matches. Searches the buckets of every type.
    /// </summary>
    /// <param name="baseKey">Key prefix to match.</param>
    /// <param name="skip">Matches to skip (paging).</param>
    /// <param name="take">Maximum keys to return.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    /// <returns>The matching keys.</returns>
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

    /// <inheritdoc/>
    public IEnumerable<CacheEntrySnapshot> GetSnapshot()
    {
        foreach (var typeEntry in _cacheSource)
        {
            var type = typeEntry.Key;
            var groupMap = typeEntry.Value;

            foreach (var groupEntry in groupMap)
            {
                var groupId = groupEntry.Key;
                var cache = groupEntry.Value;

                foreach (var key in cache.Keys)
                {
                    if (cache.TryGet(key, out var value))
                    {
                        yield return new CacheEntrySnapshot(type, groupId, key, value);
                    }
                }
            }
        }
    }
}


/// <summary>
/// <see cref="IConnectionStore"/> that keeps client connections in the process-local <see cref="IMemoryCacheProvider"/>.
/// Suitable for a single server instance; use a Redis-backed store when connections must be shared across instances.
/// </summary>
[Service(typeof(IConnectionStore))]
public class InMemoryConnectionStore : AbstractConnectionStore
{
    /// <summary>Creates the store.</summary>
    /// <param name="memoryCache">Process-local cache holding the connections.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public InMemoryConnectionStore(IMemoryCacheProvider memoryCache, ILoggerFactory loggerFactory) : base(memoryCache, loggerFactory)
    {
    }
}

/// <summary>
/// Registers InMemoryCache as ICacheProvider when cache provider is set to inmemory.
/// InMemoryCache itself is always available as IMemoryCacheProvider (used by connection stores).
/// </summary>
[Service(typeof(ICacheProvider))]
[ConditionalOnConfig("altruist:persistence:cache:provider", havingValue: "inmemory")]
public class InMemoryCacheProviderAdapter : ICacheProvider
{
    private readonly IMemoryCacheProvider _inner;

    /// <summary>Wraps the process-local cache.</summary>
    /// <param name="inner">The <see cref="IMemoryCacheProvider"/> singleton.</param>
    public InMemoryCacheProviderAdapter(IMemoryCacheProvider inner) => _inner = inner;

    /// <inheritdoc/>
    public ICacheServiceToken Token => _inner.Token;
    /// <inheritdoc/>
    public Task<bool> ContainsAsync<T>(string key, string cacheGroupId = "") where T : notnull => _inner.ContainsAsync<T>(key, cacheGroupId);
    /// <inheritdoc/>
    public Task<T?> GetAsync<T>(string key, string cacheGroupId = "") where T : notnull => _inner.GetAsync<T>(key, cacheGroupId);
    /// <inheritdoc/>
    public Task<ICursor<T>> GetAllAsync<T>(string cacheGroupId = "") where T : notnull => _inner.GetAllAsync<T>(cacheGroupId);
    /// <inheritdoc/>
    public Task<ICursor<object>> GetAllAsync(Type type, string cacheGroupId = "") => _inner.GetAllAsync(type, cacheGroupId);
    /// <inheritdoc/>
    public Task SaveAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull => _inner.SaveAsync(key, entity, cacheGroupId);
    /// <inheritdoc/>
    public Task SaveBatchAsync<T>(Dictionary<string, T> entities, string cacheGroupId = "") where T : notnull => _inner.SaveBatchAsync(entities, cacheGroupId);
    /// <inheritdoc/>
    public Task<T?> RemoveAsync<T>(string key, string cacheGroupId = "") where T : notnull => _inner.RemoveAsync<T>(key, cacheGroupId);
    /// <inheritdoc/>
    public Task RemoveAndForgetAsync<T>(string key, string cacheGroupId = "") where T : notnull => _inner.RemoveAndForgetAsync<T>(key, cacheGroupId);
    /// <inheritdoc/>
    public Task ClearAsync<T>(string cacheGroupId = "") where T : notnull => _inner.ClearAsync<T>(cacheGroupId);
    /// <inheritdoc/>
    public Task ClearAllAsync() => _inner.ClearAllAsync();
    /// <inheritdoc/>
    public IEnumerable<CacheEntrySnapshot> GetSnapshot() => _inner.GetSnapshot();
}
