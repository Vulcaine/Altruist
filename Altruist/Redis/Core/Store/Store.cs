/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Text;
using System.Text.Json;

using Altruist.Contracts;
using Altruist.InMemory;
using Altruist.Persistence;

using StackExchange.Redis;

namespace Altruist.Redis;

[Service(typeof(ICacheProvider))]
[Service(typeof(IRedisCacheProvider))]
[ConditionalOnConfig("altruist:persistence:cache:provider", havingValue: "redis")]
public sealed class RedisCacheProvider : IRedisCacheProvider
{
    private readonly InMemoryCache _memoryCache;
    private readonly IDatabase _redis;
    private readonly IConnectionMultiplexer _mux;
    private readonly Dictionary<Type, VaultDocument> _documents = new();
    private readonly Dictionary<string, VaultDocument> _typeLookup = new();

    public RedisCacheProvider(RedisConnectionFactory connectionFactory)
    {
        _memoryCache = new InMemoryCache();
        _mux = connectionFactory.Multiplexer;
        _redis = _mux.GetDatabase();

        var documents = RedisDocumentHelper.CreateDocuments(_mux);

        _documents = documents
            .GroupBy(doc => doc.Type)
            .ToDictionary(g => g.Key, g => g.Last());

        _typeLookup = documents
            .GroupBy(doc => doc.Name)
            .ToDictionary(g => g.Key, g => g.Last());

        HookRedisEvents();
    }

    public IDatabase GetDatabase() => _redis;

    private VaultDocument GetDocumentOrFail<T>()
    {
        if (_documents.TryGetValue(typeof(T), out var document) && document != null)
            return document;

        throw new KeyNotFoundException("Document not found for type " + typeof(T).Name);
    }

    #region Connection Events

    private event Action? _onConnected = () => { };
    private event Action<Exception>? _onRetryExhausted = _ => { };
    private event Action<Exception>? _onFailed = _ => { };

    public event Action? OnConnected
    {
        add => _onConnected += value;
        remove => _onConnected -= value;
    }

    public event Action<Exception>? OnRetryExhausted
    {
        add => _onRetryExhausted += value;
        remove => _onRetryExhausted -= value;
    }

    public event Action<Exception>? OnFailed
    {
        add => _onFailed += value;
        remove => _onFailed -= value;
    }

    public void RaiseConnectedEvent() => _onConnected?.Invoke();
    public void RaiseFailedEvent(Exception ex) => _onFailed?.Invoke(ex);
    public void RaiseOnRetryExhaustedEvent(Exception ex) => _onRetryExhausted?.Invoke(ex);

    private void HookRedisEvents()
    {
        _redis.Multiplexer.ConnectionRestored += (_, args) =>
        {
            if (args.ConnectionType == ConnectionType.Interactive)
                RaiseConnectedEvent();
        };
        _redis.Multiplexer.ConnectionFailed += (_, args) =>
        {
            if (args.ConnectionType == ConnectionType.Interactive)
                RaiseFailedEvent(args.Exception ?? new Exception("Connection failed"));
        };

        if (_redis.Multiplexer.IsConnected)
            RaiseConnectedEvent();
        else
            _onRetryExhausted?.Invoke(new Exception("Connection failed"));
    }

    public bool IsConnected => _redis.Multiplexer.IsConnected;

    #endregion

    #region Redis API

    private static readonly ThreadLocal<MemoryStream> _memoryStream = new(() => new MemoryStream());
    public ICacheServiceToken Token => RedisCacheServiceToken.Instance;

    public string ServiceName { get; } = "RedisCache";

    private async Task SaveObjectAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        var memoryStream = _memoryStream.Value!;
        memoryStream.Seek(0, SeekOrigin.Begin);
        memoryStream.SetLength(0);

        using (var writer = new Utf8JsonWriter(memoryStream, new JsonWriterOptions { SkipValidation = true }))
        {
            JsonSerializer.Serialize(writer, entity);
        }

        await _redis.StringSetAsync(
            $"{document.Name}{(string.IsNullOrEmpty(cacheGroupId) ? "" : $"_{cacheGroupId}")}:{key}",
            memoryStream.ToArray());
    }

    private async Task<T?> GetObjectAsync<T>(string key, string cacheGroupId = "")
    {
        var document = GetDocumentOrFail<T>();
        var json = await _redis.StringGetAsync(
            $"{document.Name}{(string.IsNullOrEmpty(cacheGroupId) ? "" : $"_{cacheGroupId}")}:{key}");

        if (json.IsNullOrEmpty)
            return default;

        ReadOnlyMemory<byte> jsonMemory = Encoding.UTF8.GetBytes(json.ToString()).AsMemory();
        var jsonSpan = jsonMemory.Span;
        var reader = new Utf8JsonReader(jsonSpan);

        string? typeInfo = null;
        while (reader.Read())
        {
            if (reader.TokenType == JsonTokenType.PropertyName &&
                reader.ValueTextEquals(document.TypePropertyName))
            {
                reader.Read();
                typeInfo = reader.GetString();
                break;
            }
        }

        if (typeInfo == null)
        {
            // Fall back to direct deserialization if no type discriminator found
            return JsonSerializer.Deserialize<T>(jsonSpan);
        }

        if (!_typeLookup.TryGetValue(typeInfo, out var typeDoc))
            return JsonSerializer.Deserialize<T>(jsonSpan);

        return (T)JsonSerializer.Deserialize(jsonSpan, typeDoc.Type)!;
    }

    public async Task<T?> GetRemoteAsync<T>(string key, string cacheGroupId = "") where T : notnull
        => await GetObjectAsync<T>(key, cacheGroupId);

    public async Task<T?> GetAsync<T>(string key, string cacheGroupId = "") where T : notnull
        => await _memoryCache.GetAsync<T>(key, cacheGroupId);

    public async Task SaveRemoteAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull
    {
        await SaveObjectAsync(key, entity, cacheGroupId);
        await SaveAsync(key, entity, cacheGroupId);
    }

    public async Task SaveAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull
        => await _memoryCache.SaveAsync(key, entity, cacheGroupId);

    public async Task SaveBatchRemoteAsync<T>(Dictionary<string, T> entities, string cacheGroupId = "") where T : notnull
    {
        var batch = _redis.CreateBatch();
        var tasks = new List<Task>();

        foreach (var (key, entity) in entities)
        {
            var serializedEntity = JsonSerializer.Serialize(entity);
            tasks.Add(batch.StringSetAsync(
                $"{key}{(string.IsNullOrEmpty(cacheGroupId) ? "" : $"_{cacheGroupId}")}",
                serializedEntity));
        }

        batch.Execute();
        await Task.WhenAll(tasks);
        await SaveBatchAsync(entities, cacheGroupId);
    }

    public async Task SaveBatchAsync<T>(Dictionary<string, T> entities, string cacheGroupId = "") where T : notnull
        => await _memoryCache.SaveBatchAsync(entities, cacheGroupId);

    public async Task<T?> RemoveRemoteAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var entity = await GetObjectAsync<T>(key, cacheGroupId);
        if (entity != null)
            await RemoveAndForgetAsync<T>(key, cacheGroupId);
        return entity;
    }

    public async Task<T?> RemoveAsync<T>(string key, string cacheGroupId = "") where T : notnull
        => await _memoryCache.RemoveAsync<T>(key, cacheGroupId);

    public async Task ClearRemoteAsync<T>(string cacheGroupId = "") where T : notnull
    {
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints().First());
        var document = GetDocumentOrFail<T>();
        var keys = server.Keys(
            pattern: $"{document.Name}{(string.IsNullOrEmpty(cacheGroupId) ? "" : $"_{cacheGroupId}")}:*").ToArray();

        if (keys.Length > 0)
            await _redis.KeyDeleteAsync(keys);

        await ClearAsync<T>(cacheGroupId);
    }

    public async Task ClearAsync<T>(string cacheGroupId = "") where T : notnull
        => await _memoryCache.ClearAsync<T>(cacheGroupId);

    public Task<ICursor<T>> GetAllRemoteAsync<T>(int batchSize = 100, string cacheGroupId = "") where T : notnull
    {
        var cursor = new RedisCacheCursor<T>(_redis, GetDocumentOrFail<T>(), batchSize, cacheGroupId);
        return Task.FromResult(cursor as ICursor<T>);
    }

    public Task<ICursor<T>> GetAllRemoteAsync<T>(string cacheGroupId = "") where T : notnull
        => GetAllRemoteAsync<T>(100, cacheGroupId);

    public Task<ICursor<T>> GetAllAsync<T>(string cacheGroupId = "") where T : notnull
        => _memoryCache.GetAllAsync<T>(cacheGroupId);

    public Task<bool> ContainsAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        return _redis.KeyExistsAsync(
            $"{document.Name}{(string.IsNullOrEmpty(cacheGroupId) ? "" : $"_{cacheGroupId}")}:{key}");
    }

    public Task<ICursor<object>> GetAllRemoteAsync(Type type, string cacheGroupId = "")
    {
        if (!_documents.TryGetValue(type, out var document))
            throw new InvalidOperationException($"Document mapping for type {type.Name} not found.");

        var cursorType = typeof(RedisCacheCursor<>).MakeGenericType(type);
        var cursor = Activator.CreateInstance(cursorType, _redis, document, 100, cacheGroupId);

        return Task.FromResult((cursor as ICursor<object>)!)
            ?? throw new InvalidOperationException("Failed to create cursor.");
    }

    public Task<ICursor<object>> GetAllAsync(Type type, string cacheGroupId = "")
        => _memoryCache.GetAllAsync(type, cacheGroupId);

    public async Task ClearAllRemoteAsync()
    {
        foreach (var document in _documents.Values)
        {
            var keys = (await Keys($"{document.Name}:*")).ToArray();
            if (keys.Length > 0)
                await _redis.KeyDeleteAsync(keys);
        }
    }

    public async Task ClearAllAsync()
        => await _memoryCache.ClearAllAsync();

    /// <summary>
    /// Sync data from memory cache to redis.
    /// </summary>
    public async Task PushAsync()
    {
        var allTypes = _documents.Keys;
        foreach (var type in allTypes)
        {
            var memoryCursor = await GetAllAsync(type);
            foreach (var item in memoryCursor)
            {
                if (item is IStoredModel storedModel)
                    await SaveRemoteAsync(storedModel.StorageId, storedModel);
            }
        }
    }

    /// <summary>
    /// Load data from redis into memory cache.
    /// </summary>
    public async Task PullAsync()
    {
        var allTypes = _documents.Keys;
        foreach (var type in allTypes)
        {
            var memoryCursor = await GetAllRemoteAsync(type);
            foreach (var item in memoryCursor)
            {
                if (item is IStoredModel storedModel)
                    await SaveAsync(storedModel.StorageId, storedModel);
            }
        }
    }

    public async Task RemoveAndForgetAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        await _redis.KeyDeleteAsync(
            $"{document.Name}{(string.IsNullOrEmpty(cacheGroupId) ? "" : $"_{cacheGroupId}")}:{key}");
        await RemoveAsync<T>(key, cacheGroupId);
    }

    public async Task<IEnumerable<RedisKey>> Keys(string pattern)
    {
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints()[0]);
        var keys = new List<RedisKey>();

        await foreach (var key in server.KeysAsync(pattern: pattern))
            keys.Add(key);

        return keys;
    }

    public async Task<IEnumerable<RedisKey>> KeysAsync<T>() where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        return await Keys(pattern: $"{document.Name}:*");
    }

    public Task ConnectAsync(int maxRetries, int delayMilliseconds)
        => throw new NotImplementedException("Redis connection is handled automatically via the Multiplexer.");

    public Task ConnectAsync(string protocol, string host, int port, int maxRetries = 30, int delayMilliseconds = 2000)
        => throw new NotImplementedException("Redis connection is handled automatically via the Multiplexer.");

    public Task ConnectAsync()
        => throw new NotImplementedException("Redis connection is handled automatically via the Multiplexer.");

    public IEnumerable<CacheEntrySnapshot> GetSnapshot()
        => _memoryCache.GetSnapshot();

    #endregion
}
