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

public interface ICursor<T> where T : notnull
{
    bool HasNext { get; }
    Task<IEnumerable<T>> NextBatch();
    IEnumerator<T> GetEnumerator();
}


/// <summary>
/// Defines a generic caching provider interface for storing, retrieving, and managing objects in a cache.
/// </summary>
public interface ICacheProvider
{

    ICacheServiceToken Token { get; }

    /// <summary>
    /// Checks whether an item with the given key exists in the cache.
    /// </summary>
    /// <typeparam name="T">The type of the cached object.</typeparam>
    /// <param name="key">The unique key associated with the cached object.</param>
    /// <returns>A task that resolves to <c>true</c> if the key exists, otherwise <c>false</c>.</returns>
    Task<bool> ContainsAsync<T>(string key, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Retrieves an item from the cache by its key.
    /// </summary>
    /// <typeparam name="T">The type of the cached object.</typeparam>
    /// <param name="key">The unique key associated with the cached object.</param>
    /// <returns>A task that resolves to the cached object, or <c>null</c> if not found.</returns>
    Task<T?> GetAsync<T>(string key, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Retrieves all cached objects of a specific type as a cursor for efficient iteration.
    /// </summary>
    /// <typeparam name="T">The type of objects to retrieve.</typeparam>
    /// <returns>A task that resolves to an <see cref="ICursor{T}"/> containing the cached objects.</returns>
    Task<ICursor<T>> GetAllAsync<T>(string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Retrieves all cached objects of a specific type dynamically.
    /// </summary>
    /// <param name="type">The type of objects to retrieve.</param>
    /// <returns>A task that resolves to an <see cref="ICursor{object}"/> containing the cached objects.</returns>
    Task<ICursor<object>> GetAllAsync(Type type, string cacheGroupId = "");

    /// <summary>
    /// Saves an object in the cache with a given key.
    /// If the key already exists, it will be updated.
    /// </summary>
    /// <typeparam name="T">The type of object to store.</typeparam>
    /// <param name="key">The unique key to associate with the object.</param>
    /// <param name="entity">The object to store.</param>
    /// <returns>A task representing the asynchronous save operation.</returns>
    Task SaveAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Saves multiple objects in the cache in a batch operation.
    /// More efficient than calling <see cref="SaveAsync{T}(string, T)"/> multiple times.
    /// </summary>
    /// <typeparam name="T">The type of objects to store.</typeparam>
    /// <param name="entities">A dictionary where the key is the cache key and the value is the object to store.</param>
    /// <returns>A task representing the asynchronous batch save operation.</returns>
    Task SaveBatchAsync<T>(Dictionary<string, T> entities, string cacheGroupId = "") where T : notnull;
    /// <summary>
    /// Removes an object from the cache and returns it.
    /// This method first retrieves the object before removing it, making it useful if the object needs to be used after deletion.
    /// </summary>
    /// <typeparam name="T">The type of object to remove.</typeparam>
    /// <param name="key">The unique key of the object to remove.</param>
    /// <returns>A task that resolves to the removed object, or <c>null</c> if the key was not found.</returns>
    Task<T?> RemoveAsync<T>(string key, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Removes an object from the cache without retrieving it first.
    /// This method is more performant than <see cref="RemoveAsync{T}(string)"/> because it avoids the overhead of fetching the object before deletion.
    /// </summary>
    /// <typeparam name="T">The type of object to remove.</typeparam>
    /// <param name="key">The unique key of the object to remove.</param>
    /// <returns>A task representing the asynchronous remove operation.</returns>
    Task RemoveAndForgetAsync<T>(string key, string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Clears all cached objects of a specific type.
    /// </summary>
    /// <typeparam name="T">The type of objects to clear.</typeparam>
    /// <returns>A task representing the asynchronous clear operation.</returns>
    Task ClearAsync<T>(string cacheGroupId = "") where T : notnull;

    /// <summary>
    /// Clears all cached objects, regardless of type.
    /// </summary>
    /// <returns>A task representing the asynchronous clear operation.</returns>
    Task ClearAllAsync();

}


/// <summary>
/// Represents a Redis-based cache provider that supports both in-memory and remote Redis operations.
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


public interface IRemoteCacheProvider : ICacheProvider, IConnectable, ISyncService
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

    Task<T?> GetRemoteAsync<T>(string key, string cacheGroupId = "") where T : notnull;

    Task SaveBatchRemoteAsync<T>(Dictionary<string, T> entities, string cacheGroupId = "") where T : notnull;

    Task<T?> RemoveRemoteAsync<T>(string key, string cacheGroupId = "") where T : notnull;

    Task ClearRemoteAsync<T>(string cacheGroupId = "") where T : notnull;

    Task ClearAllRemoteAsync();

    Task<ICursor<T>> GetAllRemoteAsync<T>(int batchSize = 100, string cacheGroupId = "") where T : notnull;

    Task<ICursor<object>> GetAllRemoteAsync(Type type, string cacheGroupId = "");
}


public interface IMemoryCacheProvider : ICacheProvider
{
}
