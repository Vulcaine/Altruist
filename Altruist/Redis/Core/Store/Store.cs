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

/// <summary>
/// Two-tier cache: a process-local <see cref="InMemoryCache"/> plus Redis. Methods without "Remote" in
/// the name (<see cref="GetAsync{T}"/>, <see cref="SaveAsync{T}"/>, <see cref="RemoveAsync{T}"/>,
/// <see cref="ClearAsync{T}"/>, <see cref="GetAllAsync{T}"/>, ...) touch only the local tier and do no
/// network I/O; the <c>*RemoteAsync</c> methods talk to Redis (and some also update the local tier).
/// </summary>
/// <remarks>
/// <para>
/// When to use: model data that must be visible to several processes or survive a restart of one.
/// For single-process caching set <c>altruist:persistence:cache:provider</c> to <c>inmemory</c> (the
/// <see cref="InMemoryCache"/> backend). For fleet coordination (node registry, claims, tickets, rate-limit
/// counters) use <see cref="IFleetBackplane"/> / <see cref="RedisFleetBackplane"/> instead. For durable
/// relational storage use the database (vault) layer, not this cache.
/// </para>
/// <para>
/// DI: singleton, registered as both <see cref="ICacheProvider"/> and <see cref="IRedisCacheProvider"/> when
/// <c>altruist:persistence:cache:provider</c> is <c>redis</c>; uses the connection of
/// <see cref="RedisConnectionFactory"/> (<c>altruist:persistence:redis:connection-string</c>).
/// </para>
/// <para>
/// Key layout: <c>{document}:{key}</c>, or <c>{document}_{cacheGroupId}:{key}</c> when a group is given,
/// where <c>{document}</c> is the <see cref="VaultDocument.Name"/> of the model type. Values are
/// System.Text.Json (default options) UTF-8 JSON strings with no expiry. Every typed method requires
/// the model type to be registered in <see cref="RedisServiceConfiguration"/> before this
/// provider is constructed, otherwise it throws <see cref="KeyNotFoundException"/>.
/// </para>
/// <para>
/// Consistency: the two tiers are not synchronised. A remote write by another process is not reflected
/// in this process's local tier, and <see cref="GetRemoteAsync{T}"/> does not fill the local tier.
/// Key enumeration uses <c>SCAN</c> on the first endpoint only (not cluster-aware).
/// </para>
/// </remarks>
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

    /// <summary>
    /// Builds the provider: snapshots the registered document types (see <see cref="RedisDocumentHelper"/>)
    /// and hooks the multiplexer's connection events.
    /// </summary>
    /// <param name="connectionFactory">Supplies the shared Redis connection.</param>
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

    /// <summary>The raw Redis database, for commands this provider does not wrap. Bypasses the key layout and the local tier.</summary>
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

    /// <summary>Raised when the interactive Redis connection is restored (and at construction if already connected, before any handler can subscribe).</summary>
    public event Action? OnConnected
    {
        add => _onConnected += value;
        remove => _onConnected -= value;
    }

    /// <summary>Raised only from the constructor when the multiplexer is not connected at that moment; reconnects continue regardless.</summary>
    public event Action<Exception>? OnRetryExhausted
    {
        add => _onRetryExhausted += value;
        remove => _onRetryExhausted -= value;
    }

    /// <summary>Raised when the interactive Redis connection fails. Raised on a StackExchange.Redis thread.</summary>
    public event Action<Exception>? OnFailed
    {
        add => _onFailed += value;
        remove => _onFailed -= value;
    }

    /// <summary>Invokes <see cref="OnConnected"/>.</summary>
    public void RaiseConnectedEvent() => _onConnected?.Invoke();
    /// <summary>Invokes <see cref="OnFailed"/> with <paramref name="ex"/>.</summary>
    /// <param name="ex">The failure.</param>
    public void RaiseFailedEvent(Exception ex) => _onFailed?.Invoke(ex);
    /// <summary>Invokes <see cref="OnRetryExhausted"/> with <paramref name="ex"/>.</summary>
    /// <param name="ex">The failure.</param>
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

    /// <summary>Whether the multiplexer currently has a live connection.</summary>
    public bool IsConnected => _redis.Multiplexer.IsConnected;

    #endregion

    #region Redis API

    private static readonly ThreadLocal<MemoryStream> _memoryStream = new(() => new MemoryStream());
    /// <summary>Always <see cref="RedisCacheServiceToken.Instance"/>.</summary>
    public ICacheServiceToken Token => RedisCacheServiceToken.Instance;

    /// <summary><c>"RedisCache"</c>.</summary>
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

    /// <summary>
    /// Reads <paramref name="key"/> from Redis only (one <c>GET</c>); <c>default</c> when missing. When the JSON
    /// holds the document's type-discriminator property (<see cref="VaultDocument.TypePropertyName"/>) naming
    /// another registered document, deserializes as that type; otherwise as <typeparamref name="T"/>.
    /// Does not populate the local tier.
    /// </summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="key">Entry key.</param>
    /// <param name="cacheGroupId">Optional group; part of the Redis key.</param>
    public async Task<T?> GetRemoteAsync<T>(string key, string cacheGroupId = "") where T : notnull
        => await GetObjectAsync<T>(key, cacheGroupId);

    /// <summary>Reads from the local in-memory tier only (no Redis call). Use <see cref="GetRemoteAsync{T}"/> to read Redis.</summary>
    /// <typeparam name="T">Model type.</typeparam>
    /// <param name="key">Entry key.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public async Task<T?> GetAsync<T>(string key, string cacheGroupId = "") where T : notnull
        => await _memoryCache.GetAsync<T>(key, cacheGroupId);

    /// <summary>
    /// Writes the JSON of <paramref name="entity"/> to Redis (no expiry, overwrites), then to the local tier.
    /// Serialized as the declared type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="key">Entry key.</param>
    /// <param name="entity">Value to store.</param>
    /// <param name="cacheGroupId">Optional group; part of the Redis key.</param>
    public async Task SaveRemoteAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull
    {
        await SaveObjectAsync(key, entity, cacheGroupId);
        await SaveAsync(key, entity, cacheGroupId);
    }

    /// <summary>Writes to the local in-memory tier only (not visible to other processes). Use <see cref="SaveRemoteAsync{T}"/> to write Redis too.</summary>
    /// <typeparam name="T">Model type.</typeparam>
    /// <param name="key">Entry key.</param>
    /// <param name="entity">Value to store.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public async Task SaveAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull
        => await _memoryCache.SaveAsync(key, entity, cacheGroupId);

    /// <summary>
    /// Writes every entry to Redis in one pipelined batch (not atomic), then to the local tier.
    /// </summary>
    /// <remarks>
    /// Unlike the other methods, the Redis key here is <c>{key}</c> or <c>{key}_{cacheGroupId}</c> without the
    /// document name, so these entries are not found by <see cref="GetRemoteAsync{T}"/>, cursors or clears.
    /// </remarks>
    /// <typeparam name="T">Model type.</typeparam>
    /// <param name="entities">Key to value map.</param>
    /// <param name="cacheGroupId">Optional group.</param>
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

    /// <summary>Writes every entry to the local in-memory tier only.</summary>
    /// <typeparam name="T">Model type.</typeparam>
    /// <param name="entities">Key to value map.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public async Task SaveBatchAsync<T>(Dictionary<string, T> entities, string cacheGroupId = "") where T : notnull
        => await _memoryCache.SaveBatchAsync(entities, cacheGroupId);

    /// <summary>
    /// Reads the entry from Redis and, when found, deletes it from Redis and the local tier
    /// (via <see cref="RemoveAndForgetAsync{T}"/>). Returns the Redis value, or <c>default</c>. Not atomic
    /// (read then delete).
    /// </summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="key">Entry key.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public async Task<T?> RemoveRemoteAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var entity = await GetObjectAsync<T>(key, cacheGroupId);
        if (entity != null)
            await RemoveAndForgetAsync<T>(key, cacheGroupId);
        return entity;
    }

    /// <summary>Removes from the local in-memory tier only and returns the removed value.</summary>
    /// <typeparam name="T">Model type.</typeparam>
    /// <param name="key">Entry key.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public async Task<T?> RemoveAsync<T>(string key, string cacheGroupId = "") where T : notnull
        => await _memoryCache.RemoveAsync<T>(key, cacheGroupId);

    /// <summary>
    /// Deletes every Redis key of <typeparamref name="T"/> in the given group (pattern
    /// <c>{document}[_{group}]:*</c>, enumerated synchronously on the first endpoint), then clears the local tier for it.
    /// </summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="cacheGroupId">Optional group.</param>
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

    /// <summary>Clears entries of <typeparamref name="T"/> from the local in-memory tier only.</summary>
    /// <typeparam name="T">Model type.</typeparam>
    /// <param name="cacheGroupId">Optional group.</param>
    public async Task ClearAsync<T>(string cacheGroupId = "") where T : notnull
        => await _memoryCache.ClearAsync<T>(cacheGroupId);

    /// <summary>
    /// Returns a lazy <see cref="RedisCacheCursor{T}"/> over the Redis entries of <typeparamref name="T"/> in the
    /// group. Nothing is read until the cursor is iterated.
    /// </summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="batchSize">Keys per batch.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public Task<ICursor<T>> GetAllRemoteAsync<T>(int batchSize = 100, string cacheGroupId = "") where T : notnull
    {
        var cursor = new RedisCacheCursor<T>(_redis, GetDocumentOrFail<T>(), batchSize, cacheGroupId);
        return Task.FromResult(cursor as ICursor<T>);
    }

    /// <summary>Same as <see cref="GetAllRemoteAsync{T}(int, string)"/> with a batch size of 100.</summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="cacheGroupId">Optional group.</param>
    public Task<ICursor<T>> GetAllRemoteAsync<T>(string cacheGroupId = "") where T : notnull
        => GetAllRemoteAsync<T>(100, cacheGroupId);

    /// <summary>Cursor over the local in-memory tier only.</summary>
    /// <typeparam name="T">Model type.</typeparam>
    /// <param name="cacheGroupId">Optional group.</param>
    public Task<ICursor<T>> GetAllAsync<T>(string cacheGroupId = "") where T : notnull
        => _memoryCache.GetAllAsync<T>(cacheGroupId);

    /// <summary>Checks Redis (not the local tier) for the key with <c>EXISTS</c>.</summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="key">Entry key.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public Task<bool> ContainsAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        return _redis.KeyExistsAsync(
            $"{document.Name}{(string.IsNullOrEmpty(cacheGroupId) ? "" : $"_{cacheGroupId}")}:{key}");
    }

    /// <summary>Non-generic form of <see cref="GetAllRemoteAsync{T}(int, string)"/> (batch size 100) for a type known at run time.</summary>
    /// <param name="type">Registered model type.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    /// <exception cref="InvalidOperationException"><paramref name="type"/> is not a registered document.</exception>
    public Task<ICursor<object>> GetAllRemoteAsync(Type type, string cacheGroupId = "")
    {
        if (!_documents.TryGetValue(type, out var document))
            throw new InvalidOperationException($"Document mapping for type {type.Name} not found.");

        var cursorType = typeof(RedisCacheCursor<>).MakeGenericType(type);
        var cursor = Activator.CreateInstance(cursorType, _redis, document, 100, cacheGroupId);

        return Task.FromResult((cursor as ICursor<object>)!)
            ?? throw new InvalidOperationException("Failed to create cursor.");
    }

    /// <summary>Non-generic cursor over the local in-memory tier only.</summary>
    /// <param name="type">Model type.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public Task<ICursor<object>> GetAllAsync(Type type, string cacheGroupId = "")
        => _memoryCache.GetAllAsync(type, cacheGroupId);

    /// <summary>
    /// Deletes the Redis keys of every registered document type that are not in a group (pattern
    /// <c>{document}:*</c>). Grouped keys and the local tier are left untouched.
    /// </summary>
    public async Task ClearAllRemoteAsync()
    {
        foreach (var document in _documents.Values)
        {
            var keys = (await Keys($"{document.Name}:*")).ToArray();
            if (keys.Length > 0)
                await _redis.KeyDeleteAsync(keys);
        }
    }

    /// <summary>Clears the whole local in-memory tier (Redis untouched).</summary>
    public async Task ClearAllAsync()
        => await _memoryCache.ClearAllAsync();

    /// <summary>Deletes the key from Redis and from the local tier without reading it first.</summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="key">Entry key.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public async Task RemoveAndForgetAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        await _redis.KeyDeleteAsync(
            $"{document.Name}{(string.IsNullOrEmpty(cacheGroupId) ? "" : $"_{cacheGroupId}")}:{key}");
        await RemoveAsync<T>(key, cacheGroupId);
    }

    /// <summary>All Redis keys matching the glob <paramref name="pattern"/> on the first endpoint (<c>SCAN</c>, fully buffered).</summary>
    /// <param name="pattern">Redis glob pattern (raw: no document prefix added).</param>
    public async Task<IEnumerable<RedisKey>> Keys(string pattern)
    {
        var server = _redis.Multiplexer.GetServer(_redis.Multiplexer.GetEndPoints()[0]);
        var keys = new List<RedisKey>();

        await foreach (var key in server.KeysAsync(pattern: pattern))
            keys.Add(key);

        return keys;
    }

    /// <inheritdoc/>
    /// <remarks>Matches <c>{document}:*</c>, i.e. ungrouped keys only.</remarks>
    public async Task<IEnumerable<RedisKey>> KeysAsync<T>() where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        return await Keys(pattern: $"{document.Name}:*");
    }

    /// <summary>Not supported: the connection is managed by the multiplexer. Always throws.</summary>
    /// <param name="maxRetries">Ignored.</param>
    /// <param name="delayMilliseconds">Ignored.</param>
    /// <exception cref="NotImplementedException">Always.</exception>
    public Task ConnectAsync(int maxRetries, int delayMilliseconds)
        => throw new NotImplementedException("Redis connection is handled automatically via the Multiplexer.");

    /// <summary>Not supported: the connection is managed by the multiplexer. Always throws.</summary>
    /// <param name="protocol">Ignored.</param>
    /// <param name="host">Ignored.</param>
    /// <param name="port">Ignored.</param>
    /// <param name="maxRetries">Ignored.</param>
    /// <param name="delayMilliseconds">Ignored.</param>
    /// <exception cref="NotImplementedException">Always.</exception>
    public Task ConnectAsync(string protocol, string host, int port, int maxRetries = 30, int delayMilliseconds = 2000)
        => throw new NotImplementedException("Redis connection is handled automatically via the Multiplexer.");

    /// <summary>Not supported: the connection is managed by the multiplexer. Always throws.</summary>
    /// <exception cref="NotImplementedException">Always.</exception>
    public Task ConnectAsync()
        => throw new NotImplementedException("Redis connection is handled automatically via the Multiplexer.");

    /// <summary>Snapshot of the local in-memory tier only (Redis entries are not included).</summary>
    public IEnumerable<CacheEntrySnapshot> GetSnapshot()
        => _memoryCache.GetSnapshot();

    #endregion
}
