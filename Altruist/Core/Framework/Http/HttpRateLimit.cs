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
    public string Name { get; set; } = "";
    public int Permit { get; set; }
    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(1);
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
public sealed class HttpRateLimitOptions
{
    public const string ConfigPath = "altruist:server:http:rate-limit";

    public SharedCounterStore Store { get; set; } = SharedCounterStore.Memory;

    public Dictionary<string, HttpRateLimitPolicy> Policies { get; } = new(StringComparer.Ordinal);

    public IEnumerable<HttpRateLimitPolicy> PathPolicies => Policies.Values.Where(p => !string.IsNullOrEmpty(p.Path));

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
    public static readonly RateLimitDecision Allow = new(true, 0);

    public static RateLimitDecision Reject(TimeSpan wait) => new(false, Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds)));
}

/// <summary>
/// The named HTTP rate limits of <see cref="HttpRateLimitOptions"/>. <c>[RateLimit("name")]</c> and
/// path policies use it; call it yourself where a limit must apply after your own checks (e.g. the
/// domain errors of a request win over its limit).
/// </summary>
public interface IRequestRateLimiter
{
    HttpRateLimitOptions Options { get; }

    /// <summary>
    /// Counts one hit of <paramref name="partitionKey"/> (an address, a principal id, ...) against
    /// <paramref name="policy"/>. A rejected hit is not counted (memory store). A policy that is not
    /// configured does not limit (a warning is logged once).
    /// </summary>
    ValueTask<RateLimitDecision> HitAsync(string policy, string partitionKey, CancellationToken cancellationToken = default);

    /// <summary>Counts one hit of the request's partition (address or principal, per the policy).</summary>
    ValueTask<RateLimitDecision> HitAsync(string policy, HttpContext context, CancellationToken cancellationToken = default);
}

/// <summary>
/// Exact sliding windows in this process: a key may have <c>permit</c> hits within any span of
/// <c>window</c>. Idle keys are swept once a minute.
/// </summary>
public sealed class SlidingWindowCounter
{
    private readonly ConcurrentDictionary<string, Window> _windows = new(StringComparer.Ordinal);
    private readonly Func<DateTime> _clock;
    private long _lastSweepTicks;

    public SlidingWindowCounter(Func<DateTime>? clock = null) => _clock = clock ?? (() => DateTime.UtcNow);

    /// <summary>Counts one hit for <paramref name="key"/> unless it is over the limit (then it is not counted).</summary>
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

    [ActivatorUtilitiesConstructor]
    public RequestRateLimiter(IFleetBackplane backplane, ILoggerFactory loggerFactory)
        : this(HttpRateLimitOptions.FromConfiguration(AppConfigLoader.Load()), backplane, loggerFactory) { }

    public RequestRateLimiter(HttpRateLimitOptions options, IFleetBackplane? backplane = null, ILoggerFactory? loggerFactory = null, Func<DateTime>? clock = null)
    {
        Options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? (() => DateTime.UtcNow);
        _memory = new SlidingWindowCounter(_clock);
        _log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RequestRateLimiter>();
        _shared = new BackplaneCounters(options.Store, backplane, _clock, _log, "HTTP rate limits");
    }

    public HttpRateLimitOptions Options { get; }

    public ValueTask<RateLimitDecision> HitAsync(string policy, HttpContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var partition = Options.Policies.TryGetValue(policy, out var p) && p.Partition == RateLimitPartition.Principal
            ? context.User.PrincipalId() ?? context.ClientIp()
            : context.ClientIp();
        return HitAsync(policy, partition, cancellationToken);
    }

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
public sealed class HttpRateLimitFilter : IAsyncActionFilter
{
    private readonly IRequestRateLimiter _limiter;
    private readonly HttpErrorWriter _errors;

    public HttpRateLimitFilter(IRequestRateLimiter limiter, HttpErrorWriter errors)
    {
        _limiter = limiter;
        _errors = errors;
    }

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
