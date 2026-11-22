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

using Altruist.Contracts;
using Altruist.Persistence;

using StackExchange.Redis;

namespace Altruist;

/// <summary>
/// Non-generic marker for <see cref="ICursor{T}"/>, so cursors of different element types can be held in one collection.
/// Has no members; use <see cref="ICursor{T}"/> to read.
/// </summary>
public interface ICursorToken
{

}

/// <summary>
/// Iterates over a set of cached entries, as returned by <see cref="ICacheProvider.GetAllAsync{T}(string)"/>,
/// <see cref="IRemoteCacheProvider.GetAllRemoteAsync{T}(int, string)"/> and the connection store.
/// </summary>
/// <remarks>
/// Iterate with <c>foreach</c> (<see cref="GetEnumerator"/>); that works for every implementation. In-memory cursors
/// enumerate the live entries and do not support <see cref="NextBatch"/>. Remote (Redis) cursors fetch batches lazily
/// and block on each batch inside <c>foreach</c>; prefer <see cref="NextBatch"/> in a <c>while (cursor.HasNext)</c>
/// loop there to stay async.
/// </remarks>
/// <typeparam name="T">The entry type.</typeparam>
/// <example>
/// <code>
/// var cursor = await cache.GetAllAsync&lt;PlayerState&gt;();
/// foreach (var state in cursor)
///     Process(state);
/// </code>
/// </example>
public interface ICursor<T> : ICursorToken where T : notnull
{
    /// <summary>
    /// Whether more entries may be available. In-memory: <c>true</c> while the source is non-empty (it does not
    /// advance). Remote: <c>false</c> once the underlying key scan is exhausted.
    /// </summary>
    bool HasNext { get; }
    /// <summary>Number of entries for in-memory cursors; <c>-1</c> (unknown) for remote cursors.</summary>
    int Count { get; }
    /// <summary>
    /// Fetches the next batch of entries (remote cursors). The in-memory cursor throws
    /// <see cref="NotImplementedException"/>; enumerate it with <c>foreach</c> instead.
    /// </summary>
    /// <returns>The next batch; empty when the cursor is exhausted.</returns>
    Task<IEnumerable<T>> NextBatch();
    /// <summary>Enumerates all remaining entries (all batches, for remote cursors). Enables <c>foreach</c>.</summary>
    IEnumerator<T> GetEnumerator();
}


/// <summary>
/// Defines a generic caching provider interface for storing, retrieving, and managing objects in a cache.
/// </summary>
/// <remarks>
/// Entries are keyed by <b>(T, cacheGroupId, key)</b>: the generic type argument is part of the key, so an entry
/// saved as <c>SaveAsync&lt;Derived&gt;</c> is not found by <c>GetAsync&lt;Base&gt;</c>. <c>cacheGroupId</c>
/// partitions entries of the same type (e.g. per room or per world); <c>""</c> is the default group.
/// <para>
/// Inject <see cref="ICacheProvider"/> (singleton) for the configured cache (<c>altruist:persistence:cache:provider</c>:
/// <c>inmemory</c> or <c>redis</c>). The <see cref="ICacheProvider"/> methods operate on a process-local in-memory
/// layer even with Redis (except where noted); to read/write the shared remote store inject
/// <see cref="IRedisCacheProvider"/> and use the <see cref="IRemoteCacheProvider"/> members. Inject
/// <see cref="IMemoryCacheProvider"/> when you want the process-local cache regardless of the configured provider
/// (note: the Redis provider keeps its own private in-memory layer, separate from that singleton).
/// </para>
/// </remarks>
/// <example>
/// <code>
/// await cache.SaveAsync("p1", state, cacheGroupId: roomId);
/// var s = await cache.GetAsync&lt;PlayerState&gt;("p1", roomId);
/// </code>
/// </example>
public interface ICacheProvider
{

    /// <summary>Identifies the cache backend (in-memory or Redis) for service/strategy selection.</summary>
    ICacheServiceToken Token { get; }

    /// <summary>
    /// Checks whether an item with the given key exists in the cache.
    /// </summary>
    /// <remarks>The Redis provider checks the remote store only, not the in-memory layer.</remarks>
    /// <typeparam name="T">The type of the cached object.</typeparam>
    /// <param name="key">The unique key associated with the cached object.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    /// <returns>A task that resolves to <c>true</c> if the key exists, otherwise <c>false</c>.</returns>
    Task<bool> ContainsAsync<T>(string key, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Retrieves an item from the cache by its key.
    /// </summary>
    /// <typeparam name="T">The type of the cached object (must match the type it was saved as).</typeparam>
    /// <param name="key">The unique key associated with the cached object.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    /// <returns>A task that resolves to the cached object, or <c>null</c> if not found.</returns>
    Task<T?> GetAsync<T>(string key, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Retrieves all cached objects of a specific type as a cursor for efficient iteration.
    /// </summary>
    /// <typeparam name="T">The type of objects to retrieve.</typeparam>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    /// <returns>A task that resolves to an <see cref="ICursor{T}"/> containing the cached objects.</returns>
    Task<ICursor<T>> GetAllAsync<T>(string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Retrieves all cached objects of a specific type dynamically. Use when the type is only known at runtime;
    /// otherwise prefer <see cref="GetAllAsync{T}(string)"/>.
    /// </summary>
    /// <param name="type">The type of objects to retrieve.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    /// <returns>A task that resolves to an <see cref="ICursor{T}"/> of <see cref="object"/> containing the cached objects.</returns>
    Task<ICursor<object>> GetAllAsync(Type type, string cacheGroupId = "");

    /// <summary>
    /// Saves an object in the cache with a given key.
    /// If the key already exists, it will be updated.
    /// </summary>
    /// <typeparam name="T">The type of object to store.</typeparam>
    /// <param name="key">The unique key to associate with the object.</param>
    /// <param name="entity">The object to store.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    /// <returns>A task representing the asynchronous save operation.</returns>
    Task SaveAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Saves multiple objects in the cache in a batch operation.
    /// More efficient than calling <see cref="SaveAsync{T}(string, T, string)"/> multiple times.
    /// </summary>
    /// <typeparam name="T">The type of objects to store.</typeparam>
    /// <param name="entities">A dictionary where the key is the cache key and the value is the object to store.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    /// <returns>A task representing the asynchronous batch save operation.</returns>
    Task SaveBatchAsync<T>(Dictionary<string, T> entities, string cacheGroupId = "") where T : notnull;
    /// <summary>
    /// Removes an object from the cache and returns it.
    /// This method first retrieves the object before removing it, making it useful if the object needs to be used after deletion.
    /// </summary>
    /// <typeparam name="T">The type of object to remove.</typeparam>
    /// <param name="key">The unique key of the object to remove.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    /// <returns>A task that resolves to the removed object, or <c>null</c> if the key was not found.</returns>
    Task<T?> RemoveAsync<T>(string key, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Removes an object from the cache without retrieving it first.
    /// This method is more performant than <see cref="RemoveAsync{T}(string, string)"/> because it avoids the overhead of fetching the object before deletion.
    /// </summary>
    /// <remarks>The Redis provider also deletes the remote key (unlike <see cref="RemoveAsync{T}(string, string)"/>, which is in-memory only there).</remarks>
    /// <typeparam name="T">The type of object to remove.</typeparam>
    /// <param name="key">The unique key of the object to remove.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    /// <returns>A task representing the asynchronous remove operation.</returns>
    Task RemoveAndForgetAsync<T>(string key, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Clears all cached objects of a specific type in one group.
    /// </summary>
    /// <typeparam name="T">The type of objects to clear.</typeparam>
    /// <param name="cacheGroupId">Group partition to clear; <c>""</c> for the default group (other groups are kept).</param>
    /// <returns>A task representing the asynchronous clear operation.</returns>
    Task ClearAsync<T>(string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Clears all cached objects, regardless of type.
    /// </summary>
    /// <returns>A task representing the asynchronous clear operation.</returns>
    Task ClearAllAsync();

    /// <summary>
    /// Enumerates every in-memory entry (type, group, key, value) for diagnostics such as the dashboard.
    /// Not a fast path; don't call per tick.
    /// </summary>
    public IEnumerable<CacheEntrySnapshot> GetSnapshot();

}


/// <summary>
/// Represents a Redis-based cache provider that supports both in-memory and remote Redis operations.
/// Registered (singleton, also as <see cref="ICacheProvider"/>) when <c>altruist:persistence:cache:provider</c> is
/// <c>redis</c>. Inject it for the Redis-only members (<see cref="KeysAsync{T}"/>); for remote cache access that does
/// not depend on Redis inject <see cref="IRemoteCacheProvider"/> (the same singleton).
/// </summary>
public interface IRedisCacheProvider : IRemoteCacheProvider
{
    /// <summary>
    /// Retrieves all keys stored in Redis for a given type.
    /// </summary>
    /// <typeparam name="T">The type of entities whose keys to retrieve.</typeparam>
    /// <returns>A task representing the asynchronous operation, returning a collection of Redis keys.</returns>
    /// <remarks>
    /// This method bypasses in-memory caching and queries Redis directly.
    /// </remarks>
    Task<IEnumerable<RedisKey>> KeysAsync<T>() where T : notnull;
}


/// <summary>
/// A two-layer cache: a process-local in-memory layer (the inherited <see cref="ICacheProvider"/> methods) plus a
/// shared remote store (the <c>*Remote*</c> methods), e.g. Redis. Use the remote methods for state that other
/// processes/nodes must see; use the inherited methods for fast local reads of data this process already holds.
/// Registered in DI (with <see cref="ICacheProvider"/> and <see cref="IRedisCacheProvider"/>, one singleton) when
/// <c>altruist:persistence:cache:provider</c> is <c>redis</c>.
/// </summary>
/// <remarks>
/// Remote entries are serialized as JSON and require a document mapping for <c>T</c> (a <c>[Vault]</c> stored model;
/// unmapped types throw <see cref="KeyNotFoundException"/>, or <see cref="InvalidOperationException"/> for the
/// runtime-typed cursor). Connection lifecycle comes from <see cref="IConnectable"/>.
/// </remarks>
public interface IRemoteCacheProvider : ICacheProvider, IConnectable
{
    /// <summary>
    /// Saves the specified entity to both external and the in-memory cache.
    /// </summary>
    /// <typeparam name="T">The type of the entity being saved.</typeparam>
    /// <param name="key">The key under which to store the entity.</param>
    /// <param name="entity">The entity to be cached.</param>
    /// <param name="cacheGroupId">Optional group identifier for logical cache separation.</param>
    /// <returns>A task representing the asynchronous save operation.</returns>
    /// <remarks>
    /// This method ensures consistency by writing to both external and in-memory cache layers.
    /// </remarks>
    Task SaveRemoteAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Reads an entity directly from the remote store (bypassing and not populating the in-memory layer).
    /// For local reads use <see cref="ICacheProvider.GetAsync{T}(string, string)"/>.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="key">The entity key.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    /// <returns>The entity, or <c>null</c> when the key is missing remotely.</returns>
    Task<T?> GetRemoteAsync<T>(string key, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Writes many entities to the remote store in one pipelined batch, then to the in-memory layer.
    /// Prefer over repeated <see cref="SaveRemoteAsync{T}(string, T, string)"/> calls for bulk writes.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="entities">Key/entity pairs to save.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    Task SaveBatchRemoteAsync<T>(Dictionary<string, T> entities, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Reads the entity from the remote store and, if it existed, deletes it from both the remote store and the
    /// in-memory layer. Use <see cref="ICacheProvider.RemoveAndForgetAsync{T}(string, string)"/> when you don't need the value.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="key">The entity key.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    /// <returns>The removed entity, or <c>null</c> when it did not exist remotely.</returns>
    Task<T?> RemoveRemoteAsync<T>(string key, string cacheGroupId = "") where T : notnull;

    /// <summary>Deletes all remote entries of <typeparamref name="T"/> in the group, then clears the matching in-memory group.</summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    Task ClearRemoteAsync<T>(string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Deletes the remote entries of every mapped document type in every group, then clears the in-memory layer
    /// (like <see cref="ClearRemoteAsync{T}(string)"/> does for one type and group).
    /// </summary>
    Task ClearAllRemoteAsync();

    /// <summary>
    /// Returns a lazy cursor over all remote entries of <typeparamref name="T"/> in the group, fetched in batches.
    /// For the local layer use <see cref="ICacheProvider.GetAllAsync{T}(string)"/>.
    /// </summary>
    /// <typeparam name="T">The entity type.</typeparam>
    /// <param name="batchSize">Entries fetched per <see cref="ICursor{T}.NextBatch"/> call.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    Task<ICursor<T>> GetAllRemoteAsync<T>(int batchSize = 100, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Runtime-typed variant of <see cref="GetAllRemoteAsync{T}(int, string)"/> (batch size 100). Throws
    /// <see cref="InvalidOperationException"/> when <paramref name="type"/> has no document mapping.
    /// </summary>
    /// <param name="type">The entity type.</param>
    /// <param name="cacheGroupId">Group partition; <c>""</c> for the default group.</param>
    Task<ICursor<object>> GetAllRemoteAsync(Type type, string cacheGroupId = "");
}

/// <summary>One in-memory cache entry as reported by <see cref="ICacheProvider.GetSnapshot"/>.</summary>
/// <param name="Type">The type the entry was saved as.</param>
/// <param name="GroupId">The cache group (<c>""</c> for the default group).</param>
/// <param name="Key">The entry key.</param>
/// <param name="Value">The cached value.</param>
public sealed record CacheEntrySnapshot(
    Type Type,
    string GroupId,
    string Key,
    object? Value);


/// <summary>
/// The process-local in-memory cache, always registered as a singleton (<c>InMemoryCache</c>) regardless of
/// <c>altruist:persistence:cache:provider</c>. Inject this for data that never needs to leave the process (the
/// connection store uses it); inject <see cref="ICacheProvider"/> to follow the configured provider.
/// </summary>
public interface IMemoryCacheProvider : ICacheProvider
{
}
