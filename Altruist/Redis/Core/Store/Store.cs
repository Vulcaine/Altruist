/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Runtime.CompilerServices;
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
/// network I/O, except <see cref="ContainsAsync{T}"/> (Redis only) and <see cref="RemoveAndForgetAsync{T}"/>
/// (both tiers); the <c>*RemoteAsync</c> methods talk to Redis (and the writes also update the local tier).
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
/// DI: one singleton, registered as <see cref="ICacheProvider"/>, <see cref="IRemoteCacheProvider"/> and
/// <see cref="IRedisCacheProvider"/> when <c>altruist:persistence:cache:provider</c> is <c>redis</c>; uses the
/// connection of <see cref="RedisConnectionFactory"/> (<c>altruist:persistence:redis:connection-string</c>, whose
/// <c>defaultDatabase</c> option selects the Redis database).
/// </para>
/// <para>
/// Key layout: <c>{document}:{key}</c>, or <c>{document}_{cacheGroupId}:{key}</c> when a group is given,
/// where <c>{document}</c> is the <see cref="VaultDocument.NameOf"/> name of the model type. Values are
/// System.Text.Json (default options) UTF-8 JSON strings with no expiry. The model types are the concrete
/// <see cref="IStoredModel"/> types of the loaded assemblies, discovered when the provider is constructed
/// (see <see cref="RedisServiceConfiguration"/>); a typed call with any other type throws
/// <see cref="KeyNotFoundException"/>.
/// </para>
/// <para>
/// Consistency: the two tiers are not synchronised. A remote write by another process is not reflected
/// in this process's local tier, and <see cref="GetRemoteAsync{T}"/> does not fill the local tier.
/// Key enumeration (<c>SCAN</c>) runs on every connected primary endpoint, so it also covers a cluster.
/// </para>
/// </remarks>
[Service(typeof(ICacheProvider))]
[Service(typeof(IRemoteCacheProvider))]
[Service(typeof(IRedisCacheProvider))]
[ConditionalOnConfig("altruist:persistence:cache:provider", havingValue: "redis")]
public sealed class RedisCacheProvider : IRedisCacheProvider
{
    private const int DefaultBatchSize = 100;
    private static readonly TimeSpan ConnectPollInterval = TimeSpan.FromMilliseconds(500);

    private readonly InMemoryCache _memoryCache;
    private readonly IDatabase _redis;
    private readonly IConnectionMultiplexer _mux;
    private readonly IReadOnlyDictionary<Type, RedisDocument> _documents;
    private readonly IReadOnlyDictionary<string, RedisDocument> _typeLookup;

    /// <summary>
    /// Builds the provider: snapshots the document types (see <see cref="RedisDocumentHelper"/>) and hooks the
    /// multiplexer's connection events.
    /// </summary>
    /// <param name="connectionFactory">Supplies the shared Redis connection.</param>
    public RedisCacheProvider(RedisConnectionFactory connectionFactory)
    {
        _memoryCache = new InMemoryCache();
        _mux = connectionFactory.Multiplexer;
        _redis = _mux.GetDatabase();

        var documents = RedisDocumentHelper.CreateDocuments();

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

    private RedisDocument GetDocumentOrFail<T>() => GetDocumentOrFail(typeof(T));

    private RedisDocument GetDocumentOrFail(Type type)
    {
        if (_documents.TryGetValue(type, out var document))
            return document;

        throw new KeyNotFoundException("Document not found for type " + type.Name);
    }

    #region Connection Events

    private event Action? _onConnected = () => { };
    private event Action<Exception>? _onRetryExhausted = _ => { };
    private event Action<Exception>? _onFailed = _ => { };

    /// <summary>
    /// Raised when the interactive Redis connection is restored, and by <see cref="ConnectAsync()"/> once it is up.
    /// Raised on a StackExchange.Redis or thread-pool thread.
    /// </summary>
    public event Action? OnConnected
    {
        add => _onConnected += value;
        remove => _onConnected -= value;
    }

    /// <summary>
    /// Raised only by <see cref="ConnectAsync(int, int)"/> when Redis is still unreachable after its attempts. The
    /// multiplexer itself reconnects forever (<see cref="InfiniteReconnectRetryPolicy"/>), so the startup path
    /// (<see cref="ConnectAsync()"/>) never raises it.
    /// </summary>
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

    // Nothing is raised here: no handler can be subscribed yet. Subscribers check IsConnected and
    // call ConnectAsync() (as ServerStatus does), which reports the connection once it is up.
    private void HookRedisEvents()
    {
        _mux.ConnectionRestored += (_, args) =>
        {
            if (args.ConnectionType == ConnectionType.Interactive)
                RaiseConnectedEvent();
        };
        _mux.ConnectionFailed += (_, args) =>
        {
            if (args.ConnectionType == ConnectionType.Interactive)
                RaiseFailedEvent(args.Exception ?? new RedisConnectionException(args.FailureType, "Redis connection failed"));
        };
    }

    /// <summary>Whether the multiplexer currently has a live connection.</summary>
    public bool IsConnected => _mux.IsConnected;

    #endregion

    #region Redis API

    /// <summary>Always <see cref="RedisCacheServiceToken.Instance"/>.</summary>
    public ICacheServiceToken Token => RedisCacheServiceToken.Instance;

    /// <summary><c>"RedisCache"</c>.</summary>
    public string ServiceName { get; } = "RedisCache";

    private static string GroupPrefix(RedisDocument document, string cacheGroupId)
        => string.IsNullOrEmpty(cacheGroupId) ? document.Name : $"{document.Name}_{cacheGroupId}";

    private static RedisKey KeyOf(RedisDocument document, string key, string cacheGroupId)
        => $"{GroupPrefix(document, cacheGroupId)}:{key}";

    private static string GroupPattern(RedisDocument document, string cacheGroupId)
        => EscapeGlob(GroupPrefix(document, cacheGroupId) + ":") + "*";

    private static string EscapeGlob(string s) =>
        s.Replace("\\", "\\\\").Replace("*", "\\*").Replace("?", "\\?").Replace("[", "\\[").Replace("]", "\\]");

    // The runtime type, so a subtype keeps its own properties and its discriminator reads it back as that subtype.
    private static byte[] Serialize<T>(T entity) where T : notnull => JsonSerializer.SerializeToUtf8Bytes(entity, entity.GetType());

    // As `type`, or as the registered document (assignable to `type`) that the value's type-discriminator
    // property names.
    private object? Deserialize(RedisValue value, Type type)
    {
        var json = (ReadOnlyMemory<byte>)value;
        var discriminator = GetDocumentOrFail(type).TypePropertyName;
        var target = type;
        if (discriminator.Length > 0 && ReadDiscriminator(json.Span, discriminator) is { } typeName
            && _typeLookup.TryGetValue(typeName, out var typeDoc) && type.IsAssignableFrom(typeDoc.Type))
            target = typeDoc.Type;
        return JsonSerializer.Deserialize(json.Span, target);
    }

    private static string? ReadDiscriminator(ReadOnlySpan<byte> json, string propertyName)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var match = reader.ValueTextEquals(propertyName);
            reader.Read();
            if (match)
                return reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            reader.Skip();
        }
        return null;
    }

    /// <summary>
    /// Reads <paramref name="key"/> from Redis only (one <c>GET</c>); <c>default</c> when missing. When the JSON
    /// holds the document's type-discriminator property (<see cref="RedisDocument.TypePropertyName"/>) naming
    /// another registered document assignable to <typeparamref name="T"/>, deserializes as that type; otherwise
    /// as <typeparamref name="T"/>. Does not populate the local tier.
    /// </summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="key">Entry key.</param>
    /// <param name="cacheGroupId">Optional group; part of the Redis key.</param>
    public async Task<T?> GetRemoteAsync<T>(string key, string cacheGroupId = "") where T : notnull
    {
        var value = await _redis.StringGetAsync(KeyOf(GetDocumentOrFail<T>(), key, cacheGroupId)).ConfigureAwait(false);
        return value.IsNullOrEmpty ? default : (T?)Deserialize(value, typeof(T));
    }

    /// <summary>Reads from the local in-memory tier only (no Redis call). Use <see cref="GetRemoteAsync{T}"/> to read Redis.</summary>
    /// <typeparam name="T">Model type.</typeparam>
    /// <param name="key">Entry key.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public async Task<T?> GetAsync<T>(string key, string cacheGroupId = "") where T : notnull
        => await _memoryCache.GetAsync<T>(key, cacheGroupId);

    /// <summary>
    /// Writes the JSON of <paramref name="entity"/> to Redis (no expiry, overwrites), then to the local tier.
    /// Serialized as its runtime type, so a subtype keeps its own properties (and, with a type discriminator, reads
    /// back as that subtype).
    /// </summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="key">Entry key.</param>
    /// <param name="entity">Value to store.</param>
    /// <param name="cacheGroupId">Optional group; part of the Redis key.</param>
    public async Task SaveRemoteAsync<T>(string key, T entity, string cacheGroupId = "") where T : notnull
    {
        await _redis.StringSetAsync(KeyOf(GetDocumentOrFail<T>(), key, cacheGroupId), Serialize(entity)).ConfigureAwait(false);
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
    /// Writes every entry to Redis in one pipelined batch (not atomic; same keys and JSON as
    /// <see cref="SaveRemoteAsync{T}"/>, so the entries are readable, listable and clearable like single saves),
    /// then to the local tier.
    /// </summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="entities">Key to value map.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public async Task SaveBatchRemoteAsync<T>(Dictionary<string, T> entities, string cacheGroupId = "") where T : notnull
    {
        var document = GetDocumentOrFail<T>();
        var batch = _redis.CreateBatch();
        var tasks = new List<Task>(entities.Count);

        foreach (var (key, entity) in entities)
            tasks.Add(batch.StringSetAsync(KeyOf(document, key, cacheGroupId), Serialize(entity)));

        batch.Execute();
        await Task.WhenAll(tasks).ConfigureAwait(false);
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
        var entity = await GetRemoteAsync<T>(key, cacheGroupId);
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
    /// <c>{document}[_{group}]:*</c>, scanned on every primary), then clears the local tier for it.
    /// </summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="cacheGroupId">Optional group.</param>
    public async Task ClearRemoteAsync<T>(string cacheGroupId = "") where T : notnull
    {
        await DeleteKeysAsync(await Keys(GroupPattern(GetDocumentOrFail<T>(), cacheGroupId)));
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
    /// <param name="batchSize">Entries per batch.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="batchSize"/> is not positive.</exception>
    public Task<ICursor<T>> GetAllRemoteAsync<T>(int batchSize = DefaultBatchSize, string cacheGroupId = "") where T : notnull
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(batchSize);
        var pattern = GroupPattern(GetDocumentOrFail<T>(), cacheGroupId);
        ICursor<T> cursor = new RedisCacheCursor<T>(_redis, ct => ScanAsync(pattern, batchSize, ct), batchSize,
            value => (T?)Deserialize(value, typeof(T)));
        return Task.FromResult(cursor);
    }

    /// <summary>Same as <see cref="GetAllRemoteAsync{T}(int, string)"/> with a batch size of 100.</summary>
    /// <typeparam name="T">Registered model type.</typeparam>
    /// <param name="cacheGroupId">Optional group.</param>
    public Task<ICursor<T>> GetAllRemoteAsync<T>(string cacheGroupId = "") where T : notnull
        => GetAllRemoteAsync<T>(DefaultBatchSize, cacheGroupId);

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
        => _redis.KeyExistsAsync(KeyOf(GetDocumentOrFail<T>(), key, cacheGroupId));

    /// <summary>
    /// Non-generic form of <see cref="GetAllRemoteAsync{T}(int, string)"/> (batch size 100) for a type known at run
    /// time; entries are deserialized as <paramref name="type"/> (or the registered subtype their discriminator names).
    /// </summary>
    /// <param name="type">Registered model type.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    /// <exception cref="InvalidOperationException"><paramref name="type"/> is not a registered document.</exception>
    public Task<ICursor<object>> GetAllRemoteAsync(Type type, string cacheGroupId = "")
    {
        if (!_documents.TryGetValue(type, out var document))
            throw new InvalidOperationException($"Document mapping for type {type.Name} not found.");

        var pattern = GroupPattern(document, cacheGroupId);
        ICursor<object> cursor = new RedisCacheCursor<object>(_redis, ct => ScanAsync(pattern, DefaultBatchSize, ct),
            DefaultBatchSize, value => Deserialize(value, type));
        return Task.FromResult(cursor);
    }

    /// <summary>Non-generic cursor over the local in-memory tier only.</summary>
    /// <param name="type">Model type.</param>
    /// <param name="cacheGroupId">Optional group.</param>
    public Task<ICursor<object>> GetAllAsync(Type type, string cacheGroupId = "")
        => _memoryCache.GetAllAsync(type, cacheGroupId);

    /// <summary>
    /// Deletes the Redis keys of every registered document type in every group (see <see cref="KeysAsync{T}"/>),
    /// then clears the whole local tier, as <see cref="ClearRemoteAsync{T}"/> does for one type and group.
    /// Other keys in the Redis database are left alone.
    /// </summary>
    public async Task ClearAllRemoteAsync()
    {
        foreach (var document in _documents.Values)
            await DeleteKeysAsync(await DocumentKeysAsync(document));
        await ClearAllAsync();
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
        await _redis.KeyDeleteAsync(KeyOf(GetDocumentOrFail<T>(), key, cacheGroupId)).ConfigureAwait(false);
        await RemoveAsync<T>(key, cacheGroupId);
    }

    /// <summary>
    /// All keys of this provider's Redis database matching the glob <paramref name="pattern"/>, scanned with
    /// <c>SCAN</c> (asynchronous, fully buffered) on every connected primary endpoint. Cost grows with the whole
    /// keyspace: keep it off hot paths.
    /// </summary>
    /// <param name="pattern">Redis glob pattern (raw: no document prefix added).</param>
    public async Task<IEnumerable<RedisKey>> Keys(string pattern)
    {
        var keys = new HashSet<RedisKey>();
        await foreach (var key in ScanAsync(pattern, DefaultBatchSize, CancellationToken.None).ConfigureAwait(false))
            keys.Add(key);
        return keys;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Ungrouped (<c>{document}:*</c>) and grouped (<c>{document}_{group}:*</c>) keys. A grouped key whose prefix also
    /// fits a registered document with a longer name (document <c>a</c> in group <c>b</c> versus document <c>a_b</c>)
    /// is attributed to the longer-named document.
    /// </remarks>
    public async Task<IEnumerable<RedisKey>> KeysAsync<T>() where T : notnull
        => await DocumentKeysAsync(GetDocumentOrFail<T>());

    private async Task<IReadOnlyList<RedisKey>> DocumentKeysAsync(RedisDocument document)
    {
        var name = document.Name;
        var longerNames = _typeLookup.Keys
            .Where(other => other.StartsWith(name + "_", StringComparison.Ordinal))
            .ToArray();

        var ungrouped = await Keys(EscapeGlob(name + ":") + "*");
        var grouped = (await Keys(EscapeGlob(name + "_") + "*"))
            .Where(key => !longerNames.Any(other => BelongsTo(key.ToString(), other)));
        return ungrouped.Concat(grouped).ToList();
    }

    private static bool BelongsTo(string key, string documentName)
        => key.StartsWith(documentName + ":", StringComparison.Ordinal)
           || key.StartsWith(documentName + "_", StringComparison.Ordinal);

    private async Task DeleteKeysAsync(IEnumerable<RedisKey> keys)
    {
        // One DEL per key: in a cluster a multi-key DEL must stay within one hash slot.
        var deletes = keys.Select(k => _redis.KeyDeleteAsync(k)).ToArray();
        await Task.WhenAll(deletes).ConfigureAwait(false);
    }

    private async IAsyncEnumerable<RedisKey> ScanAsync(string pattern, int pageSize,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        foreach (var endpoint in _mux.GetEndPoints())
        {
            var server = _mux.GetServer(endpoint);
            if (!server.IsConnected || server.IsReplica)
                continue;
            await foreach (var key in server.KeysAsync(_redis.Database, pattern, pageSize)
                               .WithCancellation(cancellationToken).ConfigureAwait(false))
                yield return key;
        }
    }

    /// <summary>
    /// Waits up to <paramref name="maxRetries"/> × <paramref name="delayMilliseconds"/> for the multiplexer (which
    /// connects and reconnects on its own) to be connected, then raises <see cref="OnConnected"/>; raises
    /// <see cref="OnRetryExhausted"/> instead when it is still down after the last attempt. Use it when startup
    /// must fail on a missing Redis; <see cref="ConnectAsync()"/> waits without a limit.
    /// </summary>
    /// <param name="maxRetries">Checks before giving up.</param>
    /// <param name="delayMilliseconds">Delay between checks.</param>
    public async Task ConnectAsync(int maxRetries, int delayMilliseconds)
    {
        for (var attempt = 0; attempt < maxRetries && !IsConnected; attempt++)
            await Task.Delay(delayMilliseconds).ConfigureAwait(false);

        if (IsConnected)
            RaiseConnectedEvent();
        else
            RaiseOnRetryExhaustedEvent(new RedisConnectionException(ConnectionFailureType.UnableToConnect,
                $"Redis was not reachable after {maxRetries} attempts."));
    }

    /// <summary>Not supported: the endpoint comes from the connection string of <see cref="RedisConnectionFactory"/>. Always throws.</summary>
    /// <param name="protocol">Ignored.</param>
    /// <param name="host">Ignored.</param>
    /// <param name="port">Ignored.</param>
    /// <param name="maxRetries">Ignored.</param>
    /// <param name="delayMilliseconds">Ignored.</param>
    /// <exception cref="NotSupportedException">Always.</exception>
    public Task ConnectAsync(string protocol, string host, int port, int maxRetries = 30, int delayMilliseconds = 2000)
        => throw new NotSupportedException("The Redis endpoint comes from altruist:persistence:redis:connection-string.");

    /// <summary>
    /// The startup path (<c>ServerStatus</c> calls it while Redis is not connected): waits, without a limit, until the
    /// multiplexer is connected, then raises <see cref="OnConnected"/>. The server's startup timeout bounds the wait.
    /// </summary>
    public async Task ConnectAsync()
    {
        while (!IsConnected)
            await Task.Delay(ConnectPollInterval).ConfigureAwait(false);
        RaiseConnectedEvent();
    }

    /// <summary>Snapshot of the local in-memory tier only (Redis entries are not included).</summary>
    public IEnumerable<CacheEntrySnapshot> GetSnapshot()
        => _memoryCache.GetSnapshot();

    #endregion
}
