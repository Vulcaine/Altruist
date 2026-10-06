/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist;

/// <summary>A server of the fleet as the others last saw it (its heartbeat).</summary>
public sealed record FleetNodeInfo
{
    public required string NodeId { get; init; }
    public string Region { get; init; } = "";
    /// <summary>Where the other servers reach it (<c>host:port</c>): relays, node-to-node calls.</summary>
    public string? InternalAddress { get; init; }
    /// <summary>Where clients reach it directly, when it has an address of its own (else through any server's relay).</summary>
    public string? PublicAddress { get; init; }
    public ServerNodeState State { get; init; }
    public double Load { get; init; }
    public double MaxLoad { get; init; }
    /// <summary>Numbers the modules publish (queue lengths per playlist, ...).</summary>
    public Dictionary<string, double> Stats { get; init; } = new();
    public DateTime SeenUtc { get; init; }

    [JsonIgnore] public bool Accepting => State == ServerNodeState.Ready;

    /// <summary>New work of this cost fits (ready and within its budget).</summary>
    public bool Fits(double cost) => Accepting && (MaxLoad <= 0 || Load + cost <= MaxLoad + 1e-9);

    public double Stat(string key) => Stats.TryGetValue(key, out var v) ? v : 0;
}

/// <summary>
/// Work handed from one server to another with its player (a queue place, a lobby): written by
/// the server that sends the player away, taken by the server the player arrives at.
/// </summary>
public sealed record FleetHandoff(string Kind, Dictionary<string, string> Data, string FromNode)
{
    public string? Get(string key) => Data.TryGetValue(key, out var v) ? v : null;
}

/// <summary>
/// The servers of one game as a fleet. Every server publishes itself (address, region, state,
/// capacity, module stats) with a heartbeat and sees the others; units of work (a player's room,
/// a lobby code) are claimed by the server that runs them, so any server can tell where they are;
/// a player moving to another server carries its work as a <see cref="FleetHandoff"/>.
/// <para>
/// One configuration serves one server and many: with the default in-memory backplane (or a
/// shared one with a single server in it) every decision stays local; as soon as more servers
/// join the shared backplane (<c>altruist:server:fleet:redis</c>) they are used, with no
/// change to the game or its configuration.
/// </para>
/// </summary>
public interface IFleet
{
    string NodeId { get; }
    string Cluster { get; }
    string Region { get; }

    /// <summary>The query parameter a client sets to reach a given server (<c>?node=</c>) through any server.</summary>
    string NodeParam { get; }

    /// <summary>The backplane is shared: other servers may exist.</summary>
    bool Shared { get; }

    /// <summary>This server, current.</summary>
    FleetNodeInfo Self { get; }

    /// <summary>Every live server (this one current, the others as of their last heartbeat), by id.</summary>
    IReadOnlyList<FleetNodeInfo> Nodes { get; }

    /// <summary>More than one live server.</summary>
    bool IsMultiNode { get; }

    FleetNodeInfo? Find(string nodeId);

    /// <summary>Publishes a number with the next heartbeat (thread-safe).</summary>
    void SetStat(string key, double value);

    void ClearStat(string key);

    /// <summary>This server runs the unit (kept while it lives; any thread).</summary>
    void Claim(string kind, string id);

    void Release(string kind, string id);

    /// <summary>Claims a unit only when no live server holds it (unique ids: lobby codes).</summary>
    Task<bool> TryClaimAsync(string kind, string id, CancellationToken cancellationToken = default);

    /// <summary>The live server that runs the unit, or null.</summary>
    Task<string?> LocateAsync(string kind, string id, CancellationToken cancellationToken = default);

    /// <summary>Hands work to the principal for when it arrives at server <paramref name="nodeId"/> (only that server takes it).</summary>
    Task PutHandoffAsync(string nodeId, string principalId, FleetHandoff handoff, CancellationToken cancellationToken = default);

    /// <summary>Takes (and removes) the work handed to this principal on this server, or null.</summary>
    Task<FleetHandoff?> TakeHandoffAsync(string principalId, CancellationToken cancellationToken = default);

    /// <summary>Publishes this server and refreshes the view of the others (the heartbeat calls it).</summary>
    Task HeartbeatAsync(CancellationToken cancellationToken = default);

    /// <summary>Leaves the fleet: removes this server and its claims (shutdown).</summary>
    Task LeaveAsync(CancellationToken cancellationToken = default);

    /// <summary>The set of live servers changed (raised after a heartbeat).</summary>
    event Action? NodesChanged;
}

/// <summary>Settings of a <see cref="Fleet"/> (config <c>altruist:server:fleet</c>).</summary>
public sealed record FleetOptions
{
    /// <summary>Servers with the same cluster name form one fleet (several games may share a backplane).</summary>
    public string Cluster { get; init; } = "default";
    public string Region { get; init; } = "";
    public TimeSpan Heartbeat { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>A server missing this many heartbeats is gone.</summary>
    public int MissedHeartbeats { get; init; } = 3;
    /// <summary>How long a claim lives without a refresh (refreshed at a third of it).</summary>
    public TimeSpan ClaimTtl { get; init; } = TimeSpan.FromSeconds(90);
    public TimeSpan HandoffTtl { get; init; } = TimeSpan.FromSeconds(60);
    public string? InternalAddress { get; init; }
    /// <summary>The server's own public address; <c>{node-id}</c> is replaced. Null: clients go through the relay.</summary>
    public string? PublicAddress { get; init; }
    public string NodeParam { get; init; } = "node";

    public TimeSpan NodeTtl => Heartbeat * MissedHeartbeats;
}

/// <summary>
/// The default <see cref="IFleet"/>. Config <c>altruist:server:fleet</c>: <c>cluster</c>,
/// <c>region</c>, <c>heartbeat</c> (2 s), <c>internal-address</c> (default: this machine's address
/// and the HTTP port, loopback when the HTTP host is loopback; <c>{ip}</c> stands for this
/// machine's address, e.g. <c>{ip}:8000</c> behind a proxy in a container), <c>public-address</c>,
/// <c>node-param</c> (<c>node</c>). The backplane is whatever <see cref="IFleetBackplane"/> is
/// registered (in-memory unless <c>altruist:server:fleet:redis</c> is set).
/// </summary>
[Service(typeof(IFleet))]
[ConditionalOnMissingService(typeof(IFleet))]
public sealed class Fleet : IFleet
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IFleetBackplane _backplane;
    private readonly IServerNode _node;
    private readonly ILogger _logger;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<string, double> _stats = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _claims = new(StringComparer.Ordinal);
    private volatile FleetNodeInfo[] _others = Array.Empty<FleetNodeInfo>();
    private DateTime _claimsRefreshedUtc = DateTime.MinValue;
    private long _lastFaultLog;
    private string _membership = "";

    [ActivatorUtilitiesConstructor]
    public Fleet(
        IFleetBackplane backplane,
        IServerNode node,
        [AppConfigValue("altruist:server:fleet:cluster", "default")] string? cluster,
        [AppConfigValue("altruist:server:fleet:region", "")] string? region,
        [AppConfigValue("altruist:server:fleet:heartbeat", "2")] double heartbeatSeconds,
        [AppConfigValue("altruist:server:fleet:internal-address", "")] string? internalAddress,
        [AppConfigValue("altruist:server:fleet:public-address", "")] string? publicAddress,
        [AppConfigValue("altruist:server:fleet:node-param", "node")] string? nodeParam,
        [AppConfigValue("altruist:server:http:host", "")] string? httpHost,
        [AppConfigValue("altruist:server:http:port", "")] string? httpPort,
        ILoggerFactory? loggerFactory = null)
        : this(backplane, node, new FleetOptions
        {
            Cluster = string.IsNullOrWhiteSpace(cluster) ? "default" : cluster.Trim(),
            Region = region?.Trim() ?? "",
            Heartbeat = TimeSpan.FromSeconds(heartbeatSeconds > 0 ? heartbeatSeconds : 2),
            InternalAddress = string.IsNullOrWhiteSpace(internalAddress)
                ? DetectInternalAddress(httpHost, httpPort)
                : internalAddress.Trim().Replace("{ip}", MachineAddress()?.ToString() ?? "127.0.0.1", StringComparison.Ordinal),
            PublicAddress = string.IsNullOrWhiteSpace(publicAddress) ? null : publicAddress.Trim(),
            NodeParam = string.IsNullOrWhiteSpace(nodeParam) ? "node" : nodeParam.Trim(),
        }, loggerFactory)
    {
    }

    public Fleet(IFleetBackplane backplane, IServerNode node, FleetOptions options, ILoggerFactory? loggerFactory = null, Func<DateTime>? utcNow = null)
    {
        _backplane = backplane;
        _node = node;
        Options = options;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<Fleet>();
    }

    public FleetOptions Options { get; }
    public IFleetBackplane Backplane => _backplane;
    public string NodeId => _node.NodeId;
    public string Cluster => Options.Cluster;
    public string Region => Options.Region;
    public string NodeParam => Options.NodeParam;
    public bool Shared => _backplane.Shared;

    public event Action? NodesChanged;

    private string Key(string rest) => $"fleet:{Options.Cluster}:{rest}";
    private string NodeKey(string id) => Key("node:" + id);
    private string UnitKey(string kind, string id) => Key($"unit:{kind}:{id}");
    private string HandoffKey(string node, string principal) => Key($"handoff:{node}:{principal}");

    // ------------------------------------------------------------------ nodes

    public FleetNodeInfo Self
    {
        get
        {
            var c = _node.Capacity();
            return new FleetNodeInfo
            {
                NodeId = NodeId,
                Region = Region,
                InternalAddress = Options.InternalAddress,
                PublicAddress = Options.PublicAddress?.Replace("{node-id}", NodeId, StringComparison.Ordinal),
                State = c.State,
                Load = c.Load,
                MaxLoad = c.MaxLoad,
                Stats = new Dictionary<string, double>(_stats, StringComparer.Ordinal),
                SeenUtc = _utcNow(),
            };
        }
    }

    public IReadOnlyList<FleetNodeInfo> Nodes
    {
        get
        {
            var cutoff = _utcNow() - Options.NodeTtl;
            var list = new List<FleetNodeInfo> { Self };
            foreach (var n in _others)
                if (n.SeenUtc > cutoff) list.Add(n);
            list.Sort((a, b) => string.CompareOrdinal(a.NodeId, b.NodeId));
            return list;
        }
    }

    public bool IsMultiNode
    {
        get
        {
            var cutoff = _utcNow() - Options.NodeTtl;
            foreach (var n in _others)
                if (n.SeenUtc > cutoff) return true;
            return false;
        }
    }

    public FleetNodeInfo? Find(string nodeId)
    {
        if (nodeId == NodeId) return Self;
        var cutoff = _utcNow() - Options.NodeTtl;
        foreach (var n in _others)
            if (n.NodeId == nodeId && n.SeenUtc > cutoff) return n;
        return null;
    }

    public void SetStat(string key, double value) => _stats[key] = value;

    public void ClearStat(string key) => _stats.TryRemove(key, out _);

    public async Task HeartbeatAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var self = Self;
            var ttl = Options.NodeTtl;
            await _backplane.SetAsync(NodeKey(NodeId), JsonSerializer.Serialize(self, Json), ttl, cancellationToken).ConfigureAwait(false);

            // Claims live much longer than a heartbeat; refresh them at a third of their life.
            if (_utcNow() - _claimsRefreshedUtc >= Options.ClaimTtl / 3 && !_claims.IsEmpty)
            {
                var values = _claims.Keys.Select(k => new KeyValuePair<string, string>(k, NodeId)).ToList();
                await _backplane.SetManyAsync(values, Options.ClaimTtl, cancellationToken).ConfigureAwait(false);
                _claimsRefreshedUtc = _utcNow();
            }

            var prefix = Key("node:");
            var found = await _backplane.GetByPrefixAsync(prefix, cancellationToken).ConfigureAwait(false);
            var others = new List<FleetNodeInfo>();
            foreach (var (key, value) in found)
            {
                if (key == NodeKey(NodeId)) continue;
                try
                {
                    if (JsonSerializer.Deserialize<FleetNodeInfo>(value, Json) is { } info) others.Add(info);
                }
                catch (JsonException ex) { LogFault(ex, "unreadable fleet node entry {Key}", key); }
            }
            others.Sort((a, b) => string.CompareOrdinal(a.NodeId, b.NodeId));
            _others = others.ToArray();
            var membership = string.Join(",", others.Select(o => o.NodeId));
            if (membership != _membership)
            {
                _membership = membership;
                _logger.LogInformation("Fleet {Cluster}: {Count} other server(s) [{Nodes}]", Cluster, others.Count, membership);
                NodesChanged?.Invoke();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The backplane is down: the others age out of the view and this server works alone.
            LogFault(ex, "fleet heartbeat failed ({Backplane}); serving alone until it is back", _backplane.Kind);
        }
    }

    public async Task LeaveAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _backplane.DeleteAsync(NodeKey(NodeId), cancellationToken).ConfigureAwait(false);
            foreach (var key in _claims.Keys.ToArray())
                await _backplane.DeleteIfValueAsync(key, NodeId, cancellationToken).ConfigureAwait(false);
            _claims.Clear();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogFault(ex, "leaving the fleet failed ({Backplane})", _backplane.Kind);
        }
    }

    // ------------------------------------------------------------------ units

    public void Claim(string kind, string id)
    {
        var key = UnitKey(kind, id);
        if (!_claims.TryAdd(key, 0)) return;
        Background(_backplane.SetAsync(key, NodeId, Options.ClaimTtl), "claim");
    }

    public void Release(string kind, string id)
    {
        var key = UnitKey(kind, id);
        if (!_claims.TryRemove(key, out _)) return;
        Background(_backplane.DeleteIfValueAsync(key, NodeId), "release");
    }

    public async Task<bool> TryClaimAsync(string kind, string id, CancellationToken cancellationToken = default)
    {
        var key = UnitKey(kind, id);
        if (_claims.ContainsKey(key)) return true;
        if (await _backplane.SetIfAbsentAsync(key, NodeId, Options.ClaimTtl, cancellationToken).ConfigureAwait(false))
        {
            _claims.TryAdd(key, 0);
            return true;
        }
        // Held by a server that is gone: take it over.
        var owner = await _backplane.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (owner is not null && owner != NodeId && Find(owner) is null)
        {
            await _backplane.SetAsync(key, NodeId, Options.ClaimTtl, cancellationToken).ConfigureAwait(false);
            _claims.TryAdd(key, 0);
            return true;
        }
        return owner == NodeId;
    }

    public async Task<string?> LocateAsync(string kind, string id, CancellationToken cancellationToken = default)
    {
        var key = UnitKey(kind, id);
        if (_claims.ContainsKey(key)) return NodeId;
        var owner = await _backplane.GetAsync(key, cancellationToken).ConfigureAwait(false);
        // A claim of a server that left (crashed) points nowhere.
        return owner is not null && Find(owner) is not null ? owner : null;
    }

    public Task PutHandoffAsync(string nodeId, string principalId, FleetHandoff handoff, CancellationToken cancellationToken = default) =>
        _backplane.SetAsync(HandoffKey(nodeId, principalId), JsonSerializer.Serialize(handoff, Json), Options.HandoffTtl, cancellationToken);

    public async Task<FleetHandoff?> TakeHandoffAsync(string principalId, CancellationToken cancellationToken = default)
    {
        var raw = await _backplane.TakeAsync(HandoffKey(NodeId, principalId), cancellationToken).ConfigureAwait(false);
        if (raw is null) return null;
        try
        {
            return JsonSerializer.Deserialize<FleetHandoff>(raw, Json);
        }
        catch (JsonException ex)
        {
            LogFault(ex, "unreadable handoff for {Principal}", principalId);
            return null;
        }
    }

    // ------------------------------------------------------------------ helpers

    private void Background(Task task, string what)
    {
        if (task.IsCompletedSuccessfully) return;
        task.ContinueWith(t => LogFault(t.Exception!.GetBaseException(), "fleet {What} failed", what),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
    }

    private void LogFault(Exception ex, string message, string arg)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastFaultLog);
        if (last != 0 && now - last < Stopwatch.Frequency * 30) return;
        Interlocked.Exchange(ref _lastFaultLog, now);
        _logger.LogWarning(ex, message, arg);
    }

    /// <summary>
    /// <c>host:port</c> other servers reach this one at: loopback when the HTTP host is loopback
    /// (servers on one machine), else this machine's first IPv4 address.
    /// </summary>
    public static string? DetectInternalAddress(string? httpHost, string? httpPort)
    {
        if (string.IsNullOrWhiteSpace(httpPort)) return null;
        var host = httpHost?.Trim() ?? "";
        if (host is "localhost" || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip)))
            return $"127.0.0.1:{httpPort}";
        if (host.Length > 0 && host is not ("0.0.0.0" or "*" or "+" or "::" or "[::]"))
            return $"{host}:{httpPort}";
        var address = MachineAddress();
        return address is null ? $"127.0.0.1:{httpPort}" : $"{address}:{httpPort}";
    }

    /// <summary>This machine's first non-loopback IPv4 address (a container's address on its network), or null.</summary>
    public static IPAddress? MachineAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Keeps this server in the fleet: a heartbeat every <c>altruist:server:fleet:heartbeat</c>
/// seconds from start-up, and leaving the fleet when the host has stopped (after the drain).
/// </summary>
[Service(typeof(IHostedService))]
public sealed class FleetHeartbeatService : IHostedService
{
    private readonly IFleet _fleet;
    private readonly TimeSpan _interval;
    private CancellationTokenSource? _stop;
    private Task? _loop;

    public FleetHeartbeatService(IFleet fleet)
    {
        _fleet = fleet;
        _interval = fleet is Fleet f ? f.Options.Heartbeat : TimeSpan.FromSeconds(2);
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _stop = new CancellationTokenSource();
        var token = _stop.Token;
        _loop = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(_interval);
            do
            {
                await _fleet.HeartbeatAsync(token).ConfigureAwait(false);
            }
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false));
        }, token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _stop?.Cancel();
        if (_loop is not null)
        {
            try
            { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        await _fleet.LeaveAsync(cancellationToken).ConfigureAwait(false);
    }
}
