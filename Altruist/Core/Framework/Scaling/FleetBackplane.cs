/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

using Microsoft.Extensions.DependencyInjection;

namespace Altruist;

/// <summary>
/// The shared store the servers of a fleet see each other through: string values with an expiry.
/// Only the control plane uses it (heartbeats every few seconds, a lookup when a player connects
/// or moves); it is never on the engine's hot path. Implementations: <see cref="InMemoryFleetBackplane"/>
/// (one process, the default) and the Redis backplane of <c>Altruist.Redis</c>
/// (config <c>altruist:server:fleet:redis</c>). Keys are namespaced by the implementation.
/// </summary>
public interface IFleetBackplane
{
    /// <summary>"memory", "redis", ...</summary>
    string Kind { get; }

    /// <summary>Other processes see what this one writes (a fleet of more than one server is possible).</summary>
    bool Shared { get; }

    Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Sets many keys with one expiry (one round trip where the store allows).</summary>
    Task SetManyAsync(IReadOnlyCollection<KeyValuePair<string, string>> values, TimeSpan ttl, CancellationToken cancellationToken = default);

    /// <summary>Sets the key only when it does not exist (or expired); true when this call set it.</summary>
    Task<bool> SetIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default);

    Task<string?> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Gets and deletes the key in one step (null when it is not there).</summary>
    Task<string?> TakeAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Every live key starting with <paramref name="prefix"/> (small key sets: the node registry).</summary>
    Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(string prefix, CancellationToken cancellationToken = default);

    Task DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Deletes the key only while it still holds <paramref name="value"/> (release your own claim).</summary>
    Task<bool> DeleteIfValueAsync(string key, string value, CancellationToken cancellationToken = default);

    /// <summary>
    /// Adds <paramref name="by"/> to the integer at <paramref name="key"/> in one atomic step and
    /// returns the new value. A key that does not exist (or expired) starts at 0 and gets
    /// <paramref name="ttl"/>; incrementing an existing key keeps its expiry (a fixed window that
    /// starts with the first increment). Shared counters: rate limits, failure lockouts. The default
    /// throws <see cref="NotSupportedException"/> (a backplane written before counters existed):
    /// those counters then stay in each server's memory.
    /// </summary>
    Task<long> IncrementAsync(string key, TimeSpan ttl, long by = 1, CancellationToken cancellationToken = default) =>
        Task.FromException<long>(new NotSupportedException($"{GetType().Name} does not implement IFleetBackplane.IncrementAsync."));
}

/// <summary>
/// The default backplane: a dictionary in this process. One server is the whole fleet
/// (<see cref="Shared"/> false), unless several fleet members in one process share an
/// instance created with <c>shared: true</c> (tests, a multi-node simulation).
/// </summary>
[Service(typeof(IFleetBackplane))]
[ConditionalOnMissingService(typeof(IFleetBackplane))]
public sealed class InMemoryFleetBackplane : IFleetBackplane
{
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    private readonly ConcurrentDictionary<string, (string Value, DateTime Expires)> _data = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private DateTime _lastSweep;

    [ActivatorUtilitiesConstructor]
    public InMemoryFleetBackplane() : this(shared: false) { }

    public InMemoryFleetBackplane(bool shared, Func<DateTime>? utcNow = null)
    {
        Shared = shared;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    public string Kind => "memory";
    public bool Shared { get; }

    private bool Live((string Value, DateTime Expires) e) => e.Expires > _utcNow();

    public Task SetAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        _data[key] = (value, _utcNow() + ttl);
        return Task.CompletedTask;
    }

    public Task SetManyAsync(IReadOnlyCollection<KeyValuePair<string, string>> values, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        var expires = _utcNow() + ttl;
        foreach (var (k, v) in values) _data[k] = (v, expires);
        return Task.CompletedTask;
    }

    public Task<bool> SetIfAbsentAsync(string key, string value, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_data.TryGetValue(key, out var e) && Live(e)) return Task.FromResult(false);
            _data[key] = (value, _utcNow() + ttl);
            return Task.FromResult(true);
        }
    }

    public Task<string?> GetAsync(string key, CancellationToken cancellationToken = default) =>
        Task.FromResult(_data.TryGetValue(key, out var e) && Live(e) ? e.Value : null);

    public Task<string?> TakeAsync(string key, CancellationToken cancellationToken = default)
    {
        lock (_gate)
            return Task.FromResult(_data.TryRemove(key, out var e) && Live(e) ? e.Value : null);
    }

    public Task<IReadOnlyDictionary<string, string>> GetByPrefixAsync(string prefix, CancellationToken cancellationToken = default)
    {
        var now = _utcNow();
        IReadOnlyDictionary<string, string> found = _data
            .Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Value.Expires > now)
            .ToDictionary(kv => kv.Key, kv => kv.Value.Value, StringComparer.Ordinal);
        return Task.FromResult(found);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        _data.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteIfValueAsync(string key, string value, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (!_data.TryGetValue(key, out var e) || e.Value != value) return Task.FromResult(false);
            return Task.FromResult(_data.TryRemove(key, out _));
        }
    }

    public Task<long> IncrementAsync(string key, TimeSpan ttl, long by = 1, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var now = _utcNow();
            // Counters are keyed per client: drop the expired ones now and then.
            if (now - _lastSweep >= SweepInterval)
            {
                _lastSweep = now;
                foreach (var (k, e) in _data)
                    if (e.Expires <= now) _data.TryRemove(k, out _);
            }
            var live = _data.TryGetValue(key, out var current) && current.Expires > now;
            var value = (live && long.TryParse(current.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0) + by;
            _data[key] = (value.ToString(System.Globalization.CultureInfo.InvariantCulture), live ? current.Expires : now + ttl);
            return Task.FromResult(value);
        }
    }
}
