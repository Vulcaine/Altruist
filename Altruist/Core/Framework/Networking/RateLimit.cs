/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist;

/// <summary>Outcome of a rate-limit check.</summary>
public enum RateVerdict
{
    /// <summary>Within the limits: the packet reaches its gate.</summary>
    Allow,

    /// <summary>Over a limit: the packet is dropped and counted as a strike.</summary>
    Drop,

    /// <summary>Too many strikes in the window: the packet is dropped and the connection closed.</summary>
    Disconnect,
}

/// <summary>
/// Names a rate limit.
/// <list type="bullet">
/// <item>On a gate handler: its bucket (<c>altruist:server:transport:rate-limit:buckets:&lt;name&gt;</c>).
/// A bucket that lists the gate in its <c>gates</c> takes precedence; gates in no bucket use the
/// <c>default-bucket</c>.</item>
/// <item>On an MVC controller or action: an HTTP policy (<c>altruist:server:http:rate-limit:policies:&lt;name&gt;</c>,
/// see <see cref="Altruist.Http.HttpRateLimitOptions"/>); controller and action policies both apply.</item>
/// </list>
/// </summary>
[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RateLimitAttribute : Attribute
{
    /// <summary>Names the rate-limit bucket (gate handler) or HTTP policy (controller/action).</summary>
    /// <param name="bucket">Bucket or policy name; must be non-empty.</param>
    /// <exception cref="ArgumentException"><paramref name="bucket"/> is null or whitespace.</exception>
    /// <example><code>
    /// [Gate("chat")]
    /// [RateLimit("chat")]   // altruist:server:transport:rate-limit:buckets:chat
    /// public Task OnChat(ChatPacket packet, string clientId) { ... }
    /// </code></example>
    public RateLimitAttribute(string bucket)
    {
        if (string.IsNullOrWhiteSpace(bucket))
            throw new ArgumentException("A bucket name is required.", nameof(bucket));
        Bucket = bucket;
    }

    /// <summary>Bucket (gate) or policy (HTTP) name.</summary>
    public string Bucket { get; }
}

/// <summary>One token bucket: <see cref="Capacity"/> tokens (the burst), refilled at <see cref="PerSecond"/>.</summary>
public sealed class RateBucketOptions
{
    /// <summary>Maximum tokens, i.e. the burst size (config key <c>capacity</c>). Buckets start full.</summary>
    public double Capacity { get; set; }
    /// <summary>Refill rate in tokens per second (config key <c>per-second</c>).</summary>
    public double PerSecond { get; set; }

    /// <summary>Gates (event names) that draw from this bucket.</summary>
    public List<string> Gates { get; set; } = new();
}

/// <summary>
/// <c>altruist:server:transport:rate-limit</c>: per-connection limits applied by
/// <see cref="RateLimitInterceptor"/> before every gate handler (and to packets for unknown events).
/// Each connection gets one token bucket per configured bucket; a packet takes one token from the
/// bucket of its gate. A packet over its bucket, or larger than <see cref="MaxPayloadBytes"/>, is dropped
/// and counts as a strike (an oversize one as <see cref="OversizeStrikes"/>); <see cref="StrikesToDisconnect"/>
/// strikes within <see cref="StrikeWindowSeconds"/> close the connection.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>Configuration section path: <c>altruist:server:transport:rate-limit</c>.</summary>
    public const string ConfigPath = "altruist:server:transport:rate-limit";

    /// <summary>Master switch (<c>enabled</c>); when false <see cref="RateLimitInterceptor"/> is not registered.</summary>
    public bool Enabled { get; set; }

    /// <summary>Payloads larger than this are dropped (0 = no limit).</summary>
    public int MaxPayloadBytes { get; set; } = 64 * 1024;

    /// <summary>How many strikes one oversize payload weighs.</summary>
    public int OversizeStrikes { get; set; } = 20;

    /// <summary>Strikes within the window that close the connection (0 = never close, only drop).</summary>
    public int StrikesToDisconnect { get; set; } = 60;

    /// <summary>Length of the strike counting window in seconds (<c>strike-window-seconds</c>); the count resets when a strike arrives after the window elapsed.</summary>
    public double StrikeWindowSeconds { get; set; } = 10;

    /// <summary>Bucket of gates that no bucket lists (and of unknown events). Not configured = unlimited.</summary>
    public string DefaultBucket { get; set; } = "default";

    /// <summary>Portal routes the limits apply to (e.g. "/game"); empty = every route.</summary>
    public List<string> Routes { get; set; } = new();

    /// <summary>Configured buckets by name (<c>buckets:&lt;name&gt;</c>).</summary>
    public Dictionary<string, RateBucketOptions> Buckets { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Reads the options from <c>altruist:server:transport:rate-limit</c> (kebab-case keys, as in config.yml).</summary>
    public static RateLimitOptions FromConfiguration(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var section = configuration.GetSection(ConfigPath);
        var options = new RateLimitOptions();
        if (!section.Exists())
            return options;

        options.Enabled = Bool(section, "enabled") ?? options.Enabled;
        options.MaxPayloadBytes = (int)(Number(section, "max-payload-bytes") ?? options.MaxPayloadBytes);
        options.OversizeStrikes = (int)(Number(section, "oversize-strikes") ?? options.OversizeStrikes);
        options.StrikesToDisconnect = (int)(Number(section, "strikes-to-disconnect") ?? options.StrikesToDisconnect);
        options.StrikeWindowSeconds = Number(section, "strike-window-seconds") ?? options.StrikeWindowSeconds;
        options.DefaultBucket = Text(section, "default-bucket") ?? options.DefaultBucket;
        options.Routes = List(section, "routes");
        foreach (var bucket in Child(section, "buckets")?.GetChildren() ?? Enumerable.Empty<IConfigurationSection>())
        {
            options.Buckets[bucket.Key] = new RateBucketOptions
            {
                Capacity = Number(bucket, "capacity") ?? 0,
                PerSecond = Number(bucket, "per-second") ?? 0,
                Gates = List(bucket, "gates"),
            };
        }
        return options;
    }

    /// <summary>A kebab-case key, or the same key without dashes (binder style, e.g. env overrides).</summary>
    private static IConfigurationSection? Child(IConfigurationSection section, string key)
    {
        var child = section.GetSection(key);
        if (child.Exists())
            return child;
        child = section.GetSection(key.Replace("-", ""));
        if (child.Exists())
            return child;
        child = section.GetSection(key.Replace('-', '_'));
        return child.Exists() ? child : null;
    }

    private static string? Text(IConfigurationSection section, string key) => Child(section, key)?.Value;

    private static double? Number(IConfigurationSection section, string key) =>
        double.TryParse(Text(section, key), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static bool? Bool(IConfigurationSection section, string key) =>
        bool.TryParse(Text(section, key), out var v) ? v : null;

    /// <summary>A YAML list, or a comma-separated value.</summary>
    private static List<string> List(IConfigurationSection section, string key)
    {
        var child = Child(section, key);
        if (child is null)
            return new List<string>();
        var items = child.GetChildren().Select(c => c.Value).Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim()).ToList();
        if (items.Count == 0 && !string.IsNullOrWhiteSpace(child.Value))
            items = child.Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return items;
    }
}

/// <summary>Per-connection rate limiting; the default implementation is <see cref="TokenBucketRateLimiter"/>.</summary>
public interface IRateLimiter
{
    /// <summary>Checks (and counts) one packet of <paramref name="clientId"/> for <paramref name="gate"/>.</summary>
    RateVerdict Check(string clientId, string gate, int payloadLength);

    /// <summary>Drops the state of a connection that is gone.</summary>
    void Forget(string clientId);
}

/// <summary>
/// Receives every rate-limit verdict of <see cref="RateLimitInterceptor"/>. Register an implementation
/// in DI (<c>[Service(typeof(IRateLimitMetrics))]</c>); none is registered by default. Called on the
/// connections' read loops: implementations must be thread-safe and cheap.
/// </summary>
public interface IRateLimitMetrics
{
    /// <summary>Called once per checked packet with the verdict (including <see cref="RateVerdict.Allow"/>).</summary>
    /// <param name="context">Packet context.</param>
    /// <param name="verdict">Limiter verdict.</param>
    void Checked(InterceptContext context, RateVerdict verdict);
}

/// <summary>
/// Token buckets per connection and bucket, as configured by <see cref="RateLimitOptions"/>. A gate's
/// bucket comes from the bucket that lists it, else from its <see cref="RateLimitAttribute"/>, else the
/// default bucket; a bucket name that is not configured is unlimited. Buckets start full.
/// </summary>
public sealed class TokenBucketRateLimiter : IRateLimiter
{
    private sealed record BucketDef(int Index, string Name, double Capacity, double PerSecond);

    private readonly RateLimitOptions _options;
    private readonly Func<double> _clock;
    private readonly BucketDef[] _defs;
    private readonly Dictionary<string, BucketDef> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BucketDef> _byGate = new(StringComparer.Ordinal);
    private readonly BucketDef? _default;
    // Only gates that have a handler are cached (unknown event names are client-chosen).
    private readonly ConcurrentDictionary<string, BucketDef?> _handlerBuckets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ClientState> _clients = new();

    /// <summary>Creates the limiter from parsed options (see <see cref="RateLimitOptions.FromConfiguration"/>).</summary>
    /// <param name="options">Rate-limit options.</param>
    /// <param name="clock">Seconds, monotonic (tests); defaults to the stopwatch.</param>
    public TokenBucketRateLimiter(RateLimitOptions options, Func<double>? clock = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _clock = clock ?? (static () => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency);
        var defs = new List<BucketDef>();
        foreach (var (name, bucket) in options.Buckets)
        {
            var def = new BucketDef(defs.Count, name, Math.Max(0, bucket.Capacity), Math.Max(0, bucket.PerSecond));
            defs.Add(def);
            _byName[name] = def;
            foreach (var gate in bucket.Gates)
                _byGate.TryAdd(gate, def);
        }
        _defs = defs.ToArray();
        _byName.TryGetValue(options.DefaultBucket ?? "", out _default);
    }

    /// <summary>Options this limiter was built from.</summary>
    public RateLimitOptions Options => _options;

    /// <summary>Name of the bucket <paramref name="gate"/> draws from (null = unlimited).</summary>
    public string? BucketOf(string gate) => BucketFor(gate)?.Name;

    /// <inheritdoc/>
    /// <remarks>Oversize payloads are rejected before any bucket is consulted. Once a connection reached <see cref="RateVerdict.Disconnect"/>, every later packet returns <see cref="RateVerdict.Drop"/> until <see cref="Forget"/>. Thread-safe (per-connection lock).</remarks>
    public RateVerdict Check(string clientId, string gate, int payloadLength)
    {
        var state = _clients.GetOrAdd(clientId, static (_, self) => new ClientState(self._clock(), self._defs.Length), this);
        lock (state)
        {
            var now = _clock();
            if (state.Closed)
                return RateVerdict.Drop;
            if (_options.MaxPayloadBytes > 0 && payloadLength > _options.MaxPayloadBytes)
                return Strike(state, now, Math.Max(1, _options.OversizeStrikes));
            var def = BucketFor(gate);
            if (def is null)
                return RateVerdict.Allow;
            var bucket = state.Buckets[def.Index] ??= new Bucket();
            if (bucket.TryTake(now, def.Capacity, def.PerSecond))
                return RateVerdict.Allow;
            return Strike(state, now, 1);
        }
    }

    /// <inheritdoc/>
    public void Forget(string clientId) => _clients.TryRemove(clientId, out _);

    private BucketDef? BucketFor(string gate)
    {
        if (_byGate.TryGetValue(gate, out var configured))
            return configured;
        if (_handlerBuckets.TryGetValue(gate, out var cached))
            return cached;
        if (!PortalGateRegistry<IPortal>.TryGetHandler(gate, out var handler))
            return _default;
        var name = handler.Method.GetCustomAttribute<RateLimitAttribute>(inherit: true)?.Bucket;
        var def = name is not null && _byName.TryGetValue(name, out var named) ? named : _default;
        _handlerBuckets[gate] = def;
        return def;
    }

    private RateVerdict Strike(ClientState s, double now, int weight)
    {
        if (now - s.StrikeWindowStart > _options.StrikeWindowSeconds)
        {
            s.StrikeWindowStart = now;
            s.Strikes = 0;
        }
        s.Strikes += weight;
        if (_options.StrikesToDisconnect <= 0 || s.Strikes < _options.StrikesToDisconnect)
            return RateVerdict.Drop;
        s.Closed = true;
        return RateVerdict.Disconnect;
    }

    private sealed class Bucket
    {
        private double _tokens = -1;
        private double _last;

        public bool TryTake(double now, double capacity, double perSecond)
        {
            if (_tokens < 0)
                _tokens = capacity;
            else
                _tokens = Math.Min(capacity, _tokens + (now - _last) * perSecond);
            _last = now;
            if (_tokens < 1)
                return false;
            _tokens -= 1;
            return true;
        }
    }

    private sealed class ClientState
    {
        public readonly Bucket?[] Buckets;
        public double StrikeWindowStart;
        public int Strikes;
        public bool Closed;

        public ClientState(double now, int buckets)
        {
            StrikeWindowStart = now;
            Buckets = new Bucket?[buckets];
        }
    }
}

/// <summary>
/// Applies the configured rate limits (<see cref="RateLimitOptions"/>) before every gate handler. Installed
/// when <c>altruist:server:transport:rate-limit:enabled: true</c>; packets over a limit are rejected,
/// and a connection with too many strikes is closed (its output side: the read loop then runs the
/// normal disconnect path). Register your own <see cref="IRateLimiter"/> in DI to replace the token buckets.
/// </summary>
[Service(typeof(IInterceptor))]
[ConditionalOnConfig("altruist:server:transport:rate-limit:enabled", havingValue: "true")]
public sealed class RateLimitInterceptor : IConnectionStateInterceptor
{
    private readonly IRateLimiter _limiter;
    private readonly string[] _routes;
    private readonly Func<string, Task> _disconnect;
    private readonly ILogger _logger;
    private readonly IRateLimitMetrics? _metrics;

    /// <summary>DI constructor: reads <see cref="RateLimitOptions"/> from configuration and closes over-limit connections through the connection store.</summary>
    /// <param name="configuration">App configuration (<c>altruist:server:transport:rate-limit</c>).</param>
    /// <param name="store">Connection store used to close offending connections.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="limiter">Optional custom limiter from DI; defaults to <see cref="TokenBucketRateLimiter"/>.</param>
    /// <param name="metrics">Optional verdict sink from DI.</param>
    public RateLimitInterceptor(IConfiguration configuration, IConnectionStore store, ILoggerFactory loggerFactory,
        IRateLimiter? limiter = null, IRateLimitMetrics? metrics = null)
        : this(RateLimitOptions.FromConfiguration(configuration), limiter, null, loggerFactory.CreateLogger<RateLimitInterceptor>(), metrics)
    {
        _disconnect = id => CloseOutputAsync(store, id);
    }

    private RateLimitInterceptor(RateLimitOptions options, IRateLimiter? limiter, Func<string, Task>? disconnect, ILogger logger,
        IRateLimitMetrics? metrics)
    {
        Options = options;
        _limiter = limiter ?? new TokenBucketRateLimiter(options);
        _routes = options.Routes.Select(NormalizeRoute).ToArray();
        _disconnect = disconnect ?? (static _ => Task.CompletedTask);
        _logger = logger;
        _metrics = metrics;
    }

    /// <summary>An interceptor over explicit options, limiter and close action (tests, manual setup).</summary>
    public static RateLimitInterceptor Create(RateLimitOptions options, IRateLimiter? limiter = null, Func<string, Task>? disconnect = null,
        ILogger? logger = null, IRateLimitMetrics? metrics = null) =>
        new(options, limiter, disconnect, logger ?? NullLogger.Instance, metrics);

    /// <summary>Effective options.</summary>
    public RateLimitOptions Options { get; }

    /// <summary>Limiter in use (custom from DI or the default token buckets).</summary>
    public IRateLimiter Limiter => _limiter;

    /// <inheritdoc/>
    /// <remarks>Skips packets without a client id and connections whose route is not in <see cref="RateLimitOptions.Routes"/> (case-insensitive, trailing slash ignored).</remarks>
    public async Task Intercept(InterceptContext context, IPacket eventData)
    {
        if (string.IsNullOrEmpty(context.ClientId))
            return;
        if (_routes.Length > 0 && Array.IndexOf(_routes, NormalizeRoute(context.Route)) < 0)
            return;
        var verdict = _limiter.Check(context.ClientId, context.EventName, context.PayloadLength);
        _metrics?.Checked(context, verdict);
        if (verdict == RateVerdict.Allow)
            return;
        context.Reject();
        if (verdict == RateVerdict.Disconnect)
        {
            _logger.LogWarning("closing connection {Client}: repeated rate-limit violations", context.ClientId);
            await _disconnect(context.ClientId);
        }
    }

    /// <inheritdoc/>
    public void Forget(string clientId) => _limiter.Forget(clientId);

    private static string NormalizeRoute(string? route)
    {
        var r = (route ?? "").Trim().TrimEnd('/');
        return r.Length == 0 ? "/" : r.ToLowerInvariant();
    }

    private async Task CloseOutputAsync(IConnectionStore store, string clientId)
    {
        try
        {
            var connection = await store.GetConnectionAsync(clientId);
            if (connection is { IsConnected: true })
                await connection.CloseOutputAsync();
        }
        catch (Exception ex)
        {
            _logger.LogDebug("closing {Client} failed: {Message}", clientId, ex.Message);
        }
    }
}
