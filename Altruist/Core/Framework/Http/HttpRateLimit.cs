/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;

using Altruist.Security;

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist.Http;

/// <summary>Who a rate-limit policy counts separately.</summary>
public enum RateLimitPartition
{
    /// <summary>The client address (<see cref="SecurityExtensions.ClientIp"/>).</summary>
    Ip,

    /// <summary>The signed-in principal (<see cref="SecurityExtensions.PrincipalId"/>; the address for anonymous requests).</summary>
    Principal,
}

/// <summary>One named limit: at most <see cref="Permit"/> hits per <see cref="Window"/> and partition.</summary>
public sealed class HttpRateLimitPolicy
{
    /// <summary>Policy name (the YAML key under <c>policies</c>); referenced by <see cref="RateLimitAttribute"/> and <see cref="IRequestRateLimiter.HitAsync(string, string, CancellationToken)"/>.</summary>
    public string Name { get; set; } = "";
    /// <summary><c>permit</c>: hits allowed per window and partition (must be at least 1).</summary>
    public int Permit { get; set; }
    /// <summary><c>window-seconds</c>: window length (must be positive).</summary>
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
    /// <summary><c>partition</c>: count per client address (<c>ip</c>, default) or per signed-in principal (<c>principal</c>).</summary>
    public RateLimitPartition Partition { get; set; } = RateLimitPartition.Ip;

    /// <summary>Applies the policy to every request under this path (middleware) instead of only where <c>[RateLimit]</c> names it.</summary>
    public string? Path { get; set; }
}

/// <summary>
/// <c>altruist:server:http:rate-limit</c>: <c>store</c> (<c>memory</c>, the default, or <c>backplane</c>:
/// see <see cref="SharedCounterStore"/>) and <c>policies: { name: { permit, window-seconds, partition: ip | principal, path? } }</c>.
/// A policy applies to the MVC controllers and actions marked <c>[RateLimit("name")]</c>, to every
/// request under its <c>path</c>, and to your own calls of <see cref="IRequestRateLimiter.HitAsync(string, string, CancellationToken)"/>.
/// Rejected requests get 429 <c>rate_limited</c> with <c>Retry-After</c> (see <see cref="HttpErrorOptions"/>).
/// </summary>
/// <remarks>
/// These limit HTTP requests only; for realtime packets on portals use the networking rate limits
/// (<see cref="RateLimitAttribute"/> on gates). Use <c>store: backplane</c> when several servers sit behind one
/// load balancer and the limit must be global. Read once at startup; invalid policies throw <see cref="ArgumentException"/>.
/// </remarks>
/// <example>
/// <code>
/// altruist:
///   server:
///     http:
///       rate-limit:
///         store: memory
///         policies:
///           login: { permit: 5, window-seconds: 60, partition: ip }
///           api:   { permit: 600, window-seconds: 60, partition: principal, path: /api }
///
/// [RateLimit("login")]
/// [HttpPost("login")]
/// public Task&lt;IActionResult&gt; Login(LoginRequest request) =&gt; ...;
/// </code>
/// </example>
public sealed class HttpRateLimitOptions
{
    /// <summary>Config section: <c>altruist:server:http:rate-limit</c>.</summary>
    public const string ConfigPath = "altruist:server:http:rate-limit";

    /// <summary><c>store</c>: where counters live (<c>memory</c> per process, or <c>backplane</c> shared across the fleet).</summary>
    public SharedCounterStore Store { get; set; } = SharedCounterStore.Memory;

    /// <summary>Policies by name (ordinal). Add through <see cref="Add"/> so they are validated.</summary>
    public Dictionary<string, HttpRateLimitPolicy> Policies { get; } = new(StringComparer.Ordinal);

    /// <summary>Policies with a <see cref="HttpRateLimitPolicy.Path"/>, applied by the hardening middleware to every request under that path.</summary>
    public IEnumerable<HttpRateLimitPolicy> PathPolicies => Policies.Values.Where(p => !string.IsNullOrEmpty(p.Path));

    /// <summary>Adds or replaces a policy (useful for hosts built in code, e.g. tests).</summary>
    /// <param name="policy">The policy.</param>
    /// <returns>This instance, for chaining.</returns>
    /// <exception cref="ArgumentException">The policy has no name, <c>Permit &lt; 1</c>, or a non-positive window.</exception>
    public HttpRateLimitOptions Add(HttpRateLimitPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(policy);
        if (string.IsNullOrWhiteSpace(policy.Name))
            throw new ArgumentException("A rate-limit policy needs a name.", nameof(policy));
        if (policy.Permit < 1 || policy.Window <= TimeSpan.Zero)
            throw new ArgumentException($"{ConfigPath}:policies:{policy.Name} needs permit >= 1 and window-seconds > 0.");
        Policies[policy.Name] = policy;
        return this;
    }

    /// <summary>Reads <c>altruist:server:http:rate-limit</c>; a missing section yields no policies (nothing limited).</summary>
    /// <param name="configuration">Configuration root.</param>
    /// <returns>The parsed options.</returns>
    /// <exception cref="ArgumentException">A policy or <c>store</c> value is invalid.</exception>
    public static HttpRateLimitOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ConfigPath);
        var options = new HttpRateLimitOptions { Store = BackplaneCounters.Parse(TokenConfig.Text(section, "store"), ConfigPath) };
        foreach (var p in section.GetSection("policies").GetChildren())
        {
            options.Add(new HttpRateLimitPolicy
            {
                Name = p.Key,
                Permit = (int)(TokenConfig.Number(p, "permit") ?? 0),
                Window = TimeSpan.FromSeconds(TokenConfig.Number(p, "window-seconds") ?? 0),
                Partition = TokenConfig.Text(p, "partition")?.ToLowerInvariant() switch
                {
                    null or "ip" => RateLimitPartition.Ip,
                    "principal" => RateLimitPartition.Principal,
                    var other => throw new ArgumentException($"{ConfigPath}:policies:{p.Key}:partition must be ip or principal, not '{other}'."),
                },
                Path = TokenConfig.Text(p, "path"),
            });
        }
        return options;
    }
}

/// <summary>The outcome of one hit: allowed, or rejected with the seconds until a hit would be allowed.</summary>
public readonly record struct RateLimitDecision(bool Allowed, int RetryAfterSeconds)
{
    /// <summary>An allowed hit.</summary>
    public static readonly RateLimitDecision Allow = new(true, 0);

    /// <summary>A rejected hit; <paramref name="wait"/> is rounded up to whole seconds, at least 1.</summary>
    /// <param name="wait">Time until a hit would be allowed.</param>
    public static RateLimitDecision Reject(TimeSpan wait) => new(false, Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)));
}

/// <summary>
/// The named HTTP rate limits of <see cref="HttpRateLimitOptions"/>. <c>[RateLimit("name")]</c> and
/// path policies use it; call it yourself where a limit must apply after your own checks (e.g. the
/// domain errors of a request win over its limit).
/// </summary>
/// <remarks>Singleton (<see cref="RequestRateLimiter"/>); thread-safe. Prefer <see cref="RateLimitAttribute"/> or a policy
/// <c>path</c> for plain request limits; inject this only for conditional or custom-keyed limits.</remarks>
/// <example>
/// <code>
/// var decision = await limiter.HitAsync("password-reset", normalizedEmail, ct);
/// if (!decision.Allowed) return StatusCode(429);
/// </code>
/// </example>
public interface IRequestRateLimiter
{
    /// <summary>The configured policies.</summary>
    HttpRateLimitOptions Options { get; }

    /// <summary>
    /// Counts one hit of <paramref name="partitionKey"/> (an address, a principal id, ...) against
    /// <paramref name="policy"/>. A rejected hit is not counted (memory store). A policy that is not
    /// configured does not limit (a warning is logged once).
    /// </summary>
    /// <param name="policy">Policy name.</param>
    /// <param name="partitionKey">What to count separately, e.g. an address, principal id or normalized email.</param>
    /// <param name="cancellationToken">Cancels a backplane call.</param>
    /// <returns>Whether the hit is allowed, and the retry delay when not.</returns>
    ValueTask<RateLimitDecision> HitAsync(string policy, string partitionKey, CancellationToken cancellationToken = default);

    /// <summary>Counts one hit of the request's partition (address or principal, per the policy).</summary>
    /// <param name="policy">Policy name.</param>
    /// <param name="context">The request; its client address or principal id is the partition key.</param>
    /// <param name="cancellationToken">Cancels a backplane call.</param>
    /// <returns>Whether the hit is allowed, and the retry delay when not.</returns>
    ValueTask<RateLimitDecision> HitAsync(string policy, HttpContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Exact sliding windows in this process: a key may have <c>permit</c> hits within any span of
/// <c>window</c>. Idle keys are swept once a minute.
/// </summary>
/// <remarks>Thread-safe; memory grows with the number of distinct active keys times <c>permit</c>. Used by
/// <see cref="RequestRateLimiter"/>; reuse it directly for any in-process sliding-window limit.</remarks>
public sealed class SlidingWindowCounter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _clock;
    private long _lastSweepTicks;

    /// <summary>Creates a counter.</summary>
    /// <param name="clock">UTC clock (for tests); defaults to <see cref="DateTime.UtcNow"/>.</param>
    public SlidingWindowCounter(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>Counts one hit for <paramref name="key"/> unless it is over the limit (then it is not counted).</summary>
    /// <param name="key">Counter key.</param>
    /// <param name="permit">Hits allowed within any span of <paramref name="window"/>.</param>
    /// <param name="window">Window length.</param>
    /// <returns>The decision; a rejection carries the time until the oldest hit leaves the window.</returns>
    public RateLimitDecision Hit(string key, int permit, TimeSpan window)
    {
        var now = _clock();
        Sweep(now);
        var w = _windows.GetOrAdd(key, _ => new Window());
        lock (w)
        {
            w.Prune(now - window);
            if (w.Hits.Count >= permit)
                return RateLimitDecision.Reject(w.Hits.Peek() + window - now);
            w.Hits.Enqueue(now);
            w.Span = window;
            return RateLimitDecision.Allow;
        }
    }

    private void Sweep(DateTime now)
    {
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.Ticks - last < TimeSpan.FromMinutes(1).Ticks || Interlocked.CompareExchange(ref _lastSweepTicks, now.Ticks, last) != last)
            return;
        foreach (var (key, w) in _windows)
        {
            lock (w)
            {
                w.Prune(now - w.Span);
                if (w.Hits.Count == 0)
                    _windows.TryRemove(key, out _);
            }
        }
    }

    private sealed class Window
    {
        public readonly Queue<DateTime> Hits = new();
        public TimeSpan Span = TimeSpan.FromMinutes(1);

        public void Prune(DateTime cutoff)
        {
            while (Hits.Count > 0 && Hits.Peek() <= cutoff)
                Hits.Dequeue();
        }
    }
}

/// <summary>
/// The default <see cref="IRequestRateLimiter"/>: sliding windows in memory, or fixed windows in the
/// fleet backplane with <c>store: backplane</c> (one limit across every server; memory while the
/// backplane is unreachable or not shared).
/// </summary>
[Service(typeof(IRequestRateLimiter))]
public sealed class RequestRateLimiter : IRequestRateLimiter
{
    private readonly SlidingWindowCounter _memory;
    private readonly BackplaneCounters _shared;
    private readonly Func<DateTime> _clock;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<string, bool> _unknown = new(StringComparer.Ordinal);

    /// <summary>DI constructor: reads <c>altruist:server:http:rate-limit</c> via <c>AppConfigLoader.Load()</c>.</summary>
    /// <param name="backplane">Fleet backplane, used when <c>store: backplane</c>.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    [ActivatorUtilitiesConstructor]
    public RequestRateLimiter(IFleetBackplane backplane, ILoggerFactory loggerFactory)
        : this(HttpRateLimitOptions.FromConfiguration(AppConfigLoader.Load()), backplane, loggerFactory) { }

    /// <summary>Creates a limiter from explicit options (tests, hosts built in code).</summary>
    /// <param name="options">The policies and store.</param>
    /// <param name="backplane">Fleet backplane for <c>store: backplane</c>; without it counters stay in memory.</param>
    /// <param name="loggerFactory">Logger factory; <c>null</c> disables logging.</param>
    /// <param name="clock">UTC clock (for tests).</param>
    public RequestRateLimiter(HttpRateLimitOptions options, IFleetBackplane? backplane = null, ILoggerFactory? loggerFactory = null, Func<DateTime>? clock = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? (() => DateTime.UtcNow);
        _memory = new SlidingWindowCounter(_clock);
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RequestRateLimiter>();
        _shared = new BackplaneCounters(options.Store, backplane, _clock, _log, "HTTP rate limits");
    }

    /// <inheritdoc/>
    public HttpRateLimitOptions Options { get; }

    /// <inheritdoc/>
    public ValueTask<RateLimitDecision> HitAsync(string policy, HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var partition = Options.Policies.TryGetValue(policy, out var p) && p.Partition == RateLimitPartition.Principal
            ? context.User.PrincipalId() ?? context.ClientIp()
            : context.ClientIp();
        return HitAsync(policy, partition, cancellationToken);
    }

    /// <inheritdoc/>
    /// <remarks>With the backplane store, windows are fixed and clock-aligned (bursts of up to 2x <c>permit</c> across a boundary are possible);
    /// on backplane errors it falls back to the in-memory sliding window.</remarks>
    public async ValueTask<RateLimitDecision> HitAsync(string policy, string partitionKey, CancellationToken cancellationToken = default)
    {
        if (!Options.Policies.TryGetValue(policy, out var p))
        {
            if (_unknown.TryAdd(policy, true))
                _log.LogWarning("Rate-limit policy '{Policy}' is not configured ({Path}:policies); it does not limit", policy, HttpRateLimitOptions.ConfigPath);
            return RateLimitDecision.Allow;
        }

        var key = $"{p.Name}:{partitionKey}";
        if (_shared.Current is { } backplane)
        {
            try
            {
                // Fixed windows aligned to the clock, so every server agrees on the current one.
                var now = _clock();
                var index = now.Ticks / p.Window.Ticks;
                var end = new DateTime((index + 1) * p.Window.Ticks, DateTimeKind.Utc);
                var count = await backplane.IncrementAsync(BackplaneCounters.Key("rate", $"{key}#{index}"), end - now + TimeSpan.FromSeconds(1),
                    1, cancellationToken).ConfigureAwait(false);
                return count <= p.Permit ? RateLimitDecision.Allow : RateLimitDecision.Reject(end - now);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _shared.Failed(ex);
            }
        }
        return _memory.Hit(key, p.Permit, p.Window);
    }
}

/// <summary>
/// Applies the <c>[RateLimit("policy")]</c> policies of an MVC controller and action (after
/// authorization and model validation, before the action runs). Registered when policies are configured.
/// </summary>
/// <remarks>Registered globally by <see cref="HttpApiConfiguration"/>; you do not add it yourself. Each distinct policy name is hit once per request.</remarks>
public sealed class HttpRateLimitFilter : IAsyncActionFilter
{
    private readonly IRequestRateLimiter _limiter;
    private readonly HttpErrorWriter _errors;

    /// <summary>Created by MVC.</summary>
    /// <param name="limiter">The rate limiter.</param>
    /// <param name="errors">Writes the 429 response.</param>
    public HttpRateLimitFilter(IRequestRateLimiter limiter, HttpErrorWriter errors)
    {
        _limiter = limiter;
        _errors = errors;
    }

    /// <inheritdoc/>
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var seen = (HashSet<string>?)null;
        foreach (var limit in context.ActionDescriptor.EndpointMetadata.OfType<RateLimitAttribute>())
        {
            if (!(seen ??= new HashSet<string>(StringComparer.Ordinal)).Add(limit.Bucket))
                continue;
            var decision = await _limiter.HitAsync(limit.Bucket, context.HttpContext, context.HttpContext.RequestAborted).ConfigureAwait(false);
            if (!decision.Allowed)
            {
                HttpErrorWriter.SetRetryAfter(context.HttpContext.Response, decision.RetryAfterSeconds);
                context.Result = _errors.Result(StatusCodes.Status429TooManyRequests, HttpErrorCodes.RateLimited);
                return;
            }
        }
        await next().ConfigureAwait(false);
    }
}
