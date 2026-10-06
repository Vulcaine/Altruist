/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Microsoft.Extensions.Logging;

namespace Altruist;

/// <summary>Where counters that guard against abuse (rate limits, failure lockouts) are kept.</summary>
public enum SharedCounterStore
{
    /// <summary>In this process (exact sliding windows; every server of a fleet counts on its own).</summary>
    Memory,

    /// <summary>
    /// In the <see cref="IFleetBackplane"/> when it is shared (Redis), so a fleet of N servers
    /// enforces one limit instead of N; fixed windows. With a process-local backplane, and while
    /// the backplane cannot be reached, the counters fall back to memory.
    /// </summary>
    Backplane,
}

/// <summary>
/// The backplane a counter store writes to, or null for memory: none was asked for, it is not
/// shared, or it failed less than <see cref="RetryAfter"/> ago (then this server counts alone).
/// </summary>
internal sealed class BackplaneCounters
{
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);

    private readonly IFleetBackplane? _backplane;
    private readonly Func<DateTime> _clock;
    private readonly ILogger _log;
    private readonly string _what;
    private long _downUntilTicks;

    public BackplaneCounters(SharedCounterStore store, IFleetBackplane? backplane, Func<DateTime> clock, ILogger log, string what)
    {
        _backplane = store == SharedCounterStore.Backplane && backplane is { Shared: true } ? backplane : null;
        _clock = clock;
        _log = log;
        _what = what;
    }

    public IFleetBackplane? Current => _backplane is not null && _clock().Ticks >= Interlocked.Read(ref _downUntilTicks) ? _backplane : null;

    public void Failed(Exception ex)
    {
        if (ex is NotSupportedException)
        {
            // A backplane without counters: never ask again.
            if (Interlocked.Exchange(ref _downUntilTicks, DateTime.MaxValue.Ticks) != DateTime.MaxValue.Ticks)
                _log.LogWarning("The fleet backplane has no shared counters: {What} are counted on each server ({Message})", _what, ex.Message);
            return;
        }
        var until = (_clock() + RetryAfter).Ticks;
        var previous = Interlocked.Exchange(ref _downUntilTicks, until);
        if (_clock().Ticks >= previous)
            _log.LogWarning(ex, "Fleet backplane unreachable: {What} are counted on this server alone for now", _what);
    }

    public static SharedCounterStore Parse(string? value, string configPath) => value?.Trim().ToLowerInvariant() switch
    {
        null or "" or "memory" => SharedCounterStore.Memory,
        "backplane" => SharedCounterStore.Backplane,
        var other => throw new ArgumentException($"{configPath}:store must be memory or backplane, not '{other}'."),
    };

    /// <summary>Backplane key for a counter (hashed: keys hold user names and addresses).</summary>
    public static string Key(string kind, string key) => $"{kind}:{Security.OpaqueToken.Hash(key)}";
}
