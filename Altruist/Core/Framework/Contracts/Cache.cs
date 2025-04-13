using Altruist.Contracts;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using StackExchange.Redis;

namespace Altruist;

public interface ICursor<T> where T : notnull
{
    List<T> Items { get; }
    bool HasNext { get; }
    Task<bool> NextBatch();
    IEnumerator<T> GetEnumerator();
}


/// <summary>
/// Defines a generic caching provider interface for storing, retrieving, and managing objects in a cache.
/// </summary>
public interface ICacheProvider
{

    bool IsConnected { get; }
    event Action? OnConnected;
    event Action<Exception> OnFailed;

    ICacheServiceToken Token { get; }

    /// <summary>
    /// Checks whether an item with the given key exists in the cache.
    /// </summary>
    /// <typeparam name="T">The type of the cached object.</typeparam>
    /// <param name="key">The unique key associated with the cached object.</param>
    /// <returns>A task that resolves to <c>true</c> if the key exists, otherwise <c>false</c>.</returns>
    Task<bool> ContainsAsync<T>(string key) where T : notnull;

    /// <summary>
    /// Retrieves an item from the cache by its key.
    /// </summary>
    /// <typeparam name="T">The type of the cached object.</typeparam>
    /// <param name="key">The unique key associated with the cached object.</param>
    /// <returns>A task that resolves to the cached object, or <c>null</c> if not found.</returns>
    Task<T?> GetAsync<T>(string key) where T : notnull;

    /// <summary>
    /// Retrieves all cached objects of a specific type as a cursor for efficient iteration.
    /// </summary>
    /// <typeparam name="T">The type of objects to retrieve.</typeparam>
    /// <returns>A task that resolves to an <see cref="ICursor{T}"/> containing the cached objects.</returns>
    Task<ICursor<T>> GetAllAsync<T>() where T : notnull;

    /// <summary>
    /// Retrieves all cached objects of a specific type dynamically.
    /// </summary>
    /// <param name="type">The type of objects to retrieve.</param>
    /// <returns>A task that resolves to an <see cref="ICursor{object}"/> containing the cached objects.</returns>
    Task<ICursor<object>> GetAllAsync(Type type);

    /// <summary>
    /// Saves an object in the cache with a given key.
    /// If the key already exists, it will be updated.
    /// </summary>
    /// <typeparam name="T">The type of object to store.</typeparam>
    /// <param name="key">The unique key to associate with the object.</param>
    /// <param name="entity">The object to store.</param>
    /// <returns>A task representing the asynchronous save operation.</returns>
    Task SaveAsync<T>(string key, T entity) where T : notnull;

    /// <summary>
    /// Saves multiple objects in the cache in a batch operation.
    /// More efficient than calling <see cref="SaveAsync{T}(string, T)"/> multiple times.
    /// </summary>
    /// <typeparam name="T">The type of objects to store.</typeparam>
    /// <param name="entities">A dictionary where the key is the cache key and the value is the object to store.</param>
    /// <returns>A task representing the asynchronous batch save operation.</returns>
    Task SaveBatchAsync<T>(Dictionary<string, T> entities) where T : notnull;

    /// <summary>
    /// Removes an object from the cache and returns it.
    /// This method first retrieves the object before removing it, making it useful if the object needs to be used after deletion.
    /// </summary>
    /// <typeparam name="T">The type of object to remove.</typeparam>
    /// <param name="key">The unique key of the object to remove.</param>
    /// <returns>A task that resolves to the removed object, or <c>null</c> if the key was not found.</returns>
    Task<T?> RemoveAsync<T>(string key) where T : notnull;

    /// <summary>
    /// Removes an object from the cache without retrieving it first.
    /// This method is more performant than <see cref="RemoveAsync{T}(string)"/> because it avoids the overhead of fetching the object before deletion.
    /// </summary>
    /// <typeparam name="T">The type of object to remove.</typeparam>
    /// <param name="key">The unique key of the object to remove.</param>
    /// <returns>A task representing the asynchronous remove operation.</returns>
    Task RemoveAndForgetAsync<T>(string key) where T : notnull;

    /// <summary>
    /// Clears all cached objects of a specific type.
    /// </summary>
    /// <typeparam name="T">The type of objects to clear.</typeparam>
    /// <returns>A task representing the asynchronous clear operation.</returns>
    Task ClearAsync<T>() where T : notnull;

    /// <summary>
    /// Clears all cached objects, regardless of type.
    /// </summary>
    /// <returns>A task representing the asynchronous clear operation.</returns>
    Task ClearAllAsync();

}


public interface IRedisCacheProvider : IExternalCacheProvider
{
    Task<IEnumerable<RedisKey>> KeysAsync<T>() where T : notnull;
}

public interface IExternalCacheProvider : ICacheProvider
{
}


public interface IMemoryCacheProvider : ICacheProvider
{
}
