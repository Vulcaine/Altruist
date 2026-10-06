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

    /// <summary>Uses an existing connection (tests, an app that already has one).</summary>
    public RedisFleetBackplane(IConnectionMultiplexer connection)
    {
        _mux = connection;
        _ownsConnection = false;
    }

    public string Kind => "redis";
    public bool Shared => true;

    private IDatabase Db => _mux.GetDatabase();
    private static RedisKey K(string key) => Prefix + key;

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        Db.StringSetAsync(K(key), value, ttl);

    public async Task SetManyAsync(IReadOnlyCollection<KeyValuePair<string, string>> values, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        if (values.Count == 0) return;
        var batch = Db.CreateBatch();
        var writes = values.Select(kv => batch.StringSetAsync(K(kv.Key), kv.Value, ttl)).ToList();
        batch.Execute();
        await Task.WhenAll(writes).ConfigureAwait(false);
    }

    public Task<bool> SetIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default) =>
        Db.StringSetAsync(K(key), value, ttl, When.NotExists);

    public async Task<string?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        var v = await Db.StringGetAsync(K(key)).ConfigureAwait(false);
        return v.IsNull ? null : v.ToString();
    }

    public async Task<string?> TakeAsync(string key, CancellationToken cancellationToken = default)
    {
        var v = await Db.StringGetDeleteAsync(K(key)).ConfigureAwait(false);
        return v.IsNull ? null : v.ToString();
    }

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

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => Db.KeyDeleteAsync(K(key));

    public async Task<bool> DeleteIfValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        var deleted = await Db.ScriptEvaluateAsync(DeleteIfValueScript, new[] { K(key) }, new RedisValue[] { value }).ConfigureAwait(false);
        return (long)deleted == 1;
    }

    public async Task<long> IncrementAsync(string key, TimeSpan ttl, long by = 1, CancellationToken cancellationToken = default)
    {
        var ms = Math.Max(1L, (long)Math.Ceiling(ttl.TotalMilliseconds));
        var value = await Db.ScriptEvaluateAsync(IncrementScript, new[] { K(key) }, new RedisValue[] { by, ms }).ConfigureAwait(false);
        return (long)value;
    }

    private static string EscapeGlob(string s) =>
        s.Replace("\\", "\\\\").Replace("*", "\\*").Replace("?", "\\?").Replace("[", "\\[").Replace("]", "\\]");

    public void Dispose()
    {
        if (_ownsConnection) _mux.Dispose();
    }
}
