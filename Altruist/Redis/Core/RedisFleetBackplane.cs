/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using StackExchange.Redis;

namespace Altruist.Redis;

/// <summary>
/// The fleet's shared backplane on Redis (config <c>altruist:server:fleet:redis</c>, a
/// StackExchange.Redis connection string; it may be the cache's Redis). Every server of a
/// fleet points at the same Redis; keys live under <c>altruist:</c>. Needs Redis 6.2+ (GETDEL).
/// A server that cannot reach Redis keeps serving on its own and rejoins the fleet when it can.
/// </summary>
/// <remarks>
/// <para>
/// When to use: run more than one server process (a fleet) and they must see each other's
/// node registry, claims, tickets and shared counters. With a single process keep the default
/// <see cref="InMemoryFleetBackplane"/> (registered automatically when no other
/// <see cref="IFleetBackplane"/> exists): it has no network I/O but is invisible to other processes.
/// This backplane is a small control-plane key/value store, not a message bus and not the data cache;
/// for cached models use the cache provider (<see cref="RedisCacheProvider"/> /
/// <see cref="ICacheProvider"/>).
/// </para>
/// <para>
/// DI: registered as a singleton <see cref="IFleetBackplane"/> only when
/// <c>altruist:server:fleet:redis</c> is present; that registration displaces
/// <see cref="InMemoryFleetBackplane"/> (which is conditional on no other backplane). It opens its own
/// <see cref="IConnectionMultiplexer"/> (separate from <see cref="RedisConnectionFactory"/>) with
/// <c>AbortOnConnectFail = false</c>, so construction does not fail when Redis is down; individual
/// calls then fault with StackExchange.Redis connection exceptions until it reconnects.
/// </para>
/// <para>
/// Every member is a network round trip (or one batched round trip) and is thread-safe; the
/// <c>cancellationToken</c> parameters are accepted for the interface but not passed to Redis.
/// Keys are stored as <c>altruist:</c> + key; values are plain Redis strings with a TTL.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // appsettings.json
/// // "altruist": { "server": { "fleet": { "redis": "redis-host:6379,password=..." } } }
/// </code>
/// </example>
[Service(typeof(IFleetBackplane))]
[ConditionalOnConfig("altruist:server:fleet:redis")]
public sealed class RedisFleetBackplane : IFleetBackplane, IDisposable
{
    private const string Prefix = "altruist:";

    private const string DeleteIfValueScript =
        "if redis.call('get', KEYS[1]) == ARGV[1] then return redis.call('del', KEYS[1]) else return 0 end";

    // INCRBY, and an expiry for a key that has none (created by this call, or left without one).
    private const string IncrementScript =
        "local v = redis.call('incrby', KEYS[1], ARGV[1]) " +
        "if redis.call('pttl', KEYS[1]) < 0 then redis.call('pexpire', KEYS[1], ARGV[2]) end return v";

    private readonly IConnectionMultiplexer _mux;
    private readonly bool _ownsConnection;

    /// <summary>
    /// The constructor DI uses: parses <paramref name="connectionString"/> and connects a new
    /// multiplexer that this instance owns (disposed by <see cref="Dispose"/>). Logs the endpoints at
    /// Information level. Does not block on Redis being reachable.
    /// </summary>
    /// <param name="connectionString">StackExchange.Redis connection string from <c>altruist:server:fleet:redis</c>.</param>
    /// <param name="loggerFactory">Optional logger factory; a null logger is used when omitted.</param>
    /// <exception cref="ArgumentException"><paramref name="connectionString"/> is null, empty or whitespace.</exception>
    [ActivatorUtilitiesConstructor]
    public RedisFleetBackplane(
        [AppConfigValue("altruist:server:fleet:redis", "")] string connectionString,
        ILoggerFactory? loggerFactory = null)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new ArgumentException("altruist:server:fleet:redis needs a Redis connection string.", nameof(connectionString));
        var logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RedisFleetBackplane>();
        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false; // start without Redis; heartbeats retry
        logger.LogInformation("Fleet backplane: Redis {Endpoints}", string.Join(", ", options.EndPoints));
        _mux = ConnectionMultiplexer.Connect(options);
        _ownsConnection = true;
    }

    /// <summary>
    /// Uses an existing connection (tests, an app that already has one). The connection is not owned:
    /// <see cref="Dispose"/> leaves it open.
    /// </summary>
    /// <param name="connection">The multiplexer to issue commands on.</param>
    public RedisFleetBackplane(IConnectionMultiplexer connection)
    {
        _mux = connection;
        _ownsConnection = false;
    }

    /// <inheritdoc/>
    /// <value>Always <c>"redis"</c>.</value>
    public string Kind => "redis";
    /// <inheritdoc/>
    /// <value>Always <c>true</c>: every process pointed at the same Redis sees the writes.</value>
    public bool Shared => true;

    private IDatabase Db => _mux.GetDatabase();
    private static RedisKey K(string key) => Prefix + key;

    /// <summary>Stores <paramref name="value"/> at <paramref name="key"/> with expiry <paramref name="ttl"/> (Redis <c>SET ... PX</c>), overwriting any existing value.</summary>
    /// <param name="key">Logical key (stored as <c>altruist:</c> + key).</param>
    /// <param name="value">String value.</param>
    /// <param name="ttl">Time to live.</param>
    /// <param name="cancellationToken">Not observed.</param>
    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        Db.StringSetAsync(K(key), value, ttl);

    /// <summary>
    /// Sets every pair with the same <paramref name="ttl"/> in one pipelined batch (one round trip).
    /// Not atomic: each <c>SET</c> succeeds or fails on its own. No-op for an empty collection.
    /// </summary>
    /// <param name="values">Key/value pairs (keys are prefixed with <c>altruist:</c>).</param>
    /// <param name="ttl">Expiry applied to every key.</param>
    /// <param name="cancellationToken">Not observed.</param>
    public async Task SetManyAsync(IReadOnlyCollection<KeyValuePair<string, string>> values, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        if (values.Count == 0) return;
        var batch = Db.CreateBatch();
        var writes = values.Select(kv => batch.StringSetAsync(K(kv.Key), kv.Value, ttl)).ToList();
        batch.Execute();
        await Task.WhenAll(writes).ConfigureAwait(false);
    }

    /// <summary>Atomic claim: <c>SET NX</c> with expiry. Returns <c>true</c> when this call created the key.</summary>
    /// <param name="key">Logical key.</param>
    /// <param name="value">Value to store (e.g. an owner id, for a later <see cref="DeleteIfValueAsync"/>).</param>
    /// <param name="ttl">Expiry of the claim.</param>
    /// <param name="cancellationToken">Not observed.</param>
    public Task<bool> SetIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        Db.StringSetAsync(K(key), value, ttl, When.NotExists);

    /// <summary>Reads the value at <paramref name="key"/>; <c>null</c> when missing or expired.</summary>
    /// <param name="key">Logical key.</param>
    /// <param name="cancellationToken">Not observed.</param>
    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var v = await Db.StringGetAsync(K(key)).ConfigureAwait(false);
        return v.IsNull ? null : v.ToString();
    }

    /// <summary>Reads and deletes the key atomically (Redis 6.2+ <c>GETDEL</c>); <c>null</c> when missing. Use for one-shot values such as tickets.</summary>
    /// <param name="key">Logical key.</param>
    /// <param name="cancellationToken">Not observed.</param>
    public async Task<string?> TakeAsync(string key, CancellationToken cancellationToken = default)
    {
        var v = await Db.StringGetDeleteAsync(K(key)).ConfigureAwait(false);
        return v.IsNull ? null : v.ToString();
    }

    /// <summary>
    /// Returns every live key that starts with <paramref name="prefix"/> (keys returned without the
    /// <c>altruist:</c> prefix). Glob characters in <paramref name="prefix"/> are escaped.
    /// </summary>
    /// <remarks>
    /// Runs a <c>SCAN</c> (page size 250) on every connected primary endpoint, then one <c>MGET</c>.
    /// Cost grows with the size of the whole Redis keyspace, not just the match count: keep it for small,
    /// infrequent reads such as the node registry, never per request. Not a snapshot: keys that expire
    /// between the scan and the read are skipped. Returns an empty dictionary when no endpoint is connected.
    /// </remarks>
    /// <param name="prefix">Logical key prefix (literal, not a pattern).</param>
    /// <param name="cancellationToken">Not observed.</param>
    public async Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var keys = new HashSet<RedisKey>();
        var pattern = Prefix + EscapeGlob(prefix) + "*";
        foreach (var endpoint in _mux.GetEndPoints())
        {
            var server = _mux.GetServer(endpoint);
            if (!server.IsConnected || server.IsReplica) continue;
            await foreach (var key in server.KeysAsync(Db.Database, pattern, pageSize: 250).ConfigureAwait(false))
                keys.Add(key);
        }
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (keys.Count == 0) return result;
        var ordered = keys.ToArray();
        var values = await Db.StringGetAsync(ordered).ConfigureAwait(false);
        for (var i = 0; i < ordered.Length; i++)
            if (!values[i].IsNull) result[ordered[i].ToString()[Prefix.Length..]] = values[i].ToString();
        return result;
    }

    /// <summary>Deletes the key unconditionally (no-op when it does not exist).</summary>
    /// <param name="key">Logical key.</param>
    /// <param name="cancellationToken">Not observed.</param>
    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Db.KeyDeleteAsync(K(key));

    /// <summary>
    /// Deletes the key only while it still holds <paramref name="value"/>, atomically via a Lua script
    /// (release your own claim without removing someone else's). Returns <c>true</c> when deleted.
    /// </summary>
    /// <param name="key">Logical key.</param>
    /// <param name="value">Expected current value.</param>
    /// <param name="cancellationToken">Not observed.</param>
    public async Task<bool> DeleteIfValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        var deleted = await Db.ScriptEvaluateAsync(DeleteIfValueScript, new[] { K(key) }, new RedisValue[] { value }).ConfigureAwait(false);
        return (long)deleted == 1;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Atomic Lua script: <c>INCRBY</c>, then <c>PEXPIRE</c> with <paramref name="ttl"/> (rounded up to
    /// whole milliseconds, at least 1 ms) when the key has no expiry. Fails if the existing value is not
    /// an integer.
    /// </remarks>
    public async Task<long> IncrementAsync(string key, TimeSpan ttl, long by = 1, CancellationToken cancellationToken = default)
    {
        var ms = Math.Max(1L, (long)Math.Ceiling(ttl.TotalMilliseconds));
        var value = await Db.ScriptEvaluateAsync(IncrementScript, new[] { K(key) }, new RedisValue[] { by, ms }).ConfigureAwait(false);
        return (long)value;
    }

    private static string EscapeGlob(string s) =>
        s.Replace("\\", "\\\\").Replace("*", "\\*").Replace("?", "\\?").Replace("[", "\\[").Replace("]", "\\]");

    /// <summary>Disposes the multiplexer when this instance created it; a connection passed in is left open.</summary>
    public void Dispose()
    {
        if (_ownsConnection) _mux.Dispose();
    }
}
