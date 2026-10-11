/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Globalization;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Security;

/// <summary><c>altruist:security:lockout:store</c>: <c>memory</c> (default) or <c>backplane</c> (see <see cref="SharedCounterStore"/>).</summary>
public sealed class FailureLockoutOptions
{
    /// <summary>Config section of the lockout settings.</summary>
    public const string ConfigPath = "altruist:security:lockout";

    /// <summary>Where failure counts and locks live (default <see cref="SharedCounterStore.Memory"/>).</summary>
    public SharedCounterStore Store { get; set; } = SharedCounterStore.Memory;

    /// <summary>Reads <see cref="ConfigPath"/>.</summary>
    public static FailureLockoutOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return new FailureLockoutOptions
        {
            Store = BackplaneCounters.Parse(TokenConfig.Text(configuration.GetSection(ConfigPath), "store"), ConfigPath),
        };
    }
}

/// <summary>
/// Locks a key (e.g. a user name plus a client address, or an address alone) after repeated
/// failures, such as failed sign-ins. The caller picks the keys and thresholds; choose keys an
/// attacker cannot use to lock out someone else (a user name alone can be).
/// Default: <see cref="FailureLockout"/> (singleton). Thread-safe.
/// </summary>
/// <remarks>
/// Use this to stop guessing against one key (credentials, codes); to cap request volume per client use a rate
/// limiter instead. Typical flow: check <see cref="LockedForAsync"/> first, record failures, clear on success.
/// </remarks>
/// <example>
/// <code>
/// var key = $"login:{name}:{HttpContext.ClientIp()}";
/// if (await lockout.LockedForAsync(key) is { } left &amp;&amp; left &gt; TimeSpan.Zero)
///     return StatusCode(429);
/// if (!hasher.Verify(password, account?.PasswordHash))
/// {
///     await lockout.RecordFailureAsync(key, maxFailures: 5, window: TimeSpan.FromMinutes(15), lockFor: TimeSpan.FromMinutes(15));
///     return Unauthorized();
/// }
/// await lockout.ClearAsync(key);
/// </code>
/// </example>
public interface IFailureLockout
{
    /// <summary>How long the key stays locked (<see cref="TimeSpan.Zero"/> = not locked).</summary>
    /// <param name="key">The lockout key.</param>
    /// <param name="cancellationToken">Cancels a backplane call.</param>
    ValueTask<TimeSpan> LockedForAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records a failure; true when this one reached <paramref name="maxFailures"/> within
    /// <paramref name="window"/> and locked the key for <paramref name="lockFor"/> (the count starts over).
    /// Does not check an existing lock: call <see cref="LockedForAsync"/> first.
    /// </summary>
    /// <param name="key">The lockout key.</param>
    /// <param name="maxFailures">Failures within <paramref name="window"/> that trigger the lock.</param>
    /// <param name="window">Counting window (sliding in memory; fixed from the first failure in the backplane).</param>
    /// <param name="lockFor">How long the lock lasts.</param>
    /// <param name="cancellationToken">Cancels a backplane call.</param>
    ValueTask<bool> RecordFailureAsync(string key, int maxFailures, TimeSpan window, TimeSpan lockFor, CancellationToken cancellationToken = default);

    /// <summary>Forgets the key's failures and lock (e.g. after a successful sign-in).</summary>
    /// <param name="key">The lockout key.</param>
    /// <param name="cancellationToken">Cancels a backplane call.</param>
    ValueTask ClearAsync(string key, CancellationToken cancellationToken = default);
}

/// <summary>
/// The default <see cref="IFailureLockout"/>: sliding failure windows in memory, or with
/// <c>store: backplane</c> a failure count per window (starting at the first failure) and the lock
/// in the fleet backplane, so every server of a fleet sees them (memory while it is unreachable or not shared).
/// </summary>
[Service(typeof(IFailureLockout))]
public sealed class FailureLockout : IFailureLockout
{
    private readonly ConcurrentDictionary<string, Failures> _failures = new(StringComparer.Ordinal);
    private readonly BackplaneCounters _shared;
    private readonly Func<DateTime> _clock;
    private long _lastSweepTicks;

    /// <summary>DI constructor: options from <see cref="FailureLockoutOptions.ConfigPath"/>.</summary>
    [ActivatorUtilitiesConstructor]
    public FailureLockout(IFleetBackplane backplane, ILoggerFactory loggerFactory)
        : this(FailureLockoutOptions.FromConfiguration(AppConfigLoader.Load()), backplane, loggerFactory) { }

    /// <summary>Creates a lockout with explicit options (tests, or use outside DI).</summary>
    /// <param name="options">Store selection (memory when null).</param>
    /// <param name="backplane">Fleet backplane for <c>store: backplane</c>; optional.</param>
    /// <param name="loggerFactory">Logger factory; optional.</param>
    /// <param name="clock">UTC clock (tests); default <see cref="DateTime.UtcNow"/>.</param>
    public FailureLockout(FailureLockoutOptions? options = null, IFleetBackplane? backplane = null, ILoggerFactory? loggerFactory = null, Func<DateTime>? clock = null)
    {
        Options = options ?? new FailureLockoutOptions();
        _clock = clock ?? (() => DateTime.UtcNow);
        _shared = new BackplaneCounters(Options.Store, backplane, _clock,
            (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<FailureLockout>(), "failure lockouts");
    }

    /// <summary>The settings in effect.</summary>
    public FailureLockoutOptions Options { get; }

    private static string FailKey(string key) => BackplaneCounters.Key("lockout-failures", key);
    private static string LockKey(string key) => BackplaneCounters.Key("lockout", key);

    /// <inheritdoc/>
    public async ValueTask<TimeSpan> LockedForAsync(string key, CancellationToken cancellationToken = default)
    {
        if (_shared.Current is { } backplane)
        {
            try
            {
                var until = await backplane.GetAsync(LockKey(key), cancellationToken).ConfigureAwait(false);
                return until is not null && long.TryParse(until, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                    ? Left(new DateTime(ticks, DateTimeKind.Utc)) : TimeSpan.Zero;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _shared.Failed(ex);
            }
        }

        if (!_failures.TryGetValue(key, out var f))
            return TimeSpan.Zero;
        lock (f)
            return Left(f.LockedUntil);
    }

    /// <inheritdoc/>
    public async ValueTask<bool> RecordFailureAsync(string key, int maxFailures, TimeSpan window, TimeSpan lockFor, CancellationToken cancellationToken = default)
    {
        if (_shared.Current is { } backplane)
        {
            try
            {
                var count = await backplane.IncrementAsync(FailKey(key), window, 1, cancellationToken).ConfigureAwait(false);
                if (count < maxFailures)
                    return false;
                var until = _clock() + lockFor;
                await backplane.SetAsync(LockKey(key), until.Ticks.ToString(CultureInfo.InvariantCulture), lockFor, cancellationToken).ConfigureAwait(false);
                await backplane.DeleteAsync(FailKey(key), cancellationToken).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _shared.Failed(ex);
            }
        }

        var now = _clock();
        Sweep(now);
        var f = _failures.GetOrAdd(key, _ => new Failures());
        lock (f)
        {
            while (f.Times.Count > 0 && f.Times.Peek() < now - window)
                f.Times.Dequeue();
            f.Times.Enqueue(now);
            f.Window = window;
            if (f.Times.Count < maxFailures)
                return false;
            f.Times.Clear();
            f.LockedUntil = now + lockFor;
            return true;
        }
    }

    /// <inheritdoc/>
    public async ValueTask ClearAsync(string key, CancellationToken cancellationToken = default)
    {
        _failures.TryRemove(key, out _);
        if (_shared.Current is { } backplane)
        {
            try
            {
                await backplane.DeleteAsync(FailKey(key), cancellationToken).ConfigureAwait(false);
                await backplane.DeleteAsync(LockKey(key), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _shared.Failed(ex);
            }
        }
    }

    private TimeSpan Left(DateTime until)
    {
        var left = until - _clock();
        return left > TimeSpan.Zero ? left : TimeSpan.Zero;
    }

    private void Sweep(DateTime now)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.Ticks - last < TimeSpan.FromMinutes(1).Ticks || Interlocked.CompareExchange(ref _lastSweepTicks, now.Ticks, last) != last)
            return;
        foreach (var (key, f) in _failures)
        {
            lock (f)
            {
                if (f.LockedUntil < now && (f.Times.Count == 0 || f.Times.Last() < now - f.Window))
                    _failures.TryRemove(key, out _);
            }
        }
    }

    private sealed class Failures
    {
        public readonly Queue<DateTime> Times = new();
        public DateTime LockedUntil = DateTime.MinValue;
        public TimeSpan Window;
    }
}
