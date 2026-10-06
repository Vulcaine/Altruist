/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Diagnostics;
using System.Diagnostics.Metrics;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Altruist;

/// <summary>Settings of a <see cref="ServerNode"/> (config <c>altruist:server:capacity</c> and <c>altruist:server:drain</c>).</summary>
public sealed record ServerNodeOptions
{
    /// <summary>Load budget (<c>capacity:max-load</c>); 0 = unlimited.</summary>
    public double MaxLoad { get; init; }

    /// <summary>How long a drain lets running work finish (<c>drain:timeout</c>, seconds).</summary>
    public TimeSpan DrainTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>After a timed-out drain, how long the stopped work gets to wind down (<c>drain:force-grace</c>, seconds).</summary>
    public TimeSpan ForceStopGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>How often a drain checks its participants.</summary>
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(50);
}

/// <summary>
/// The default <see cref="IServerNode"/>. Config:
/// <c>altruist:server:capacity:max-load</c> (0 = unlimited),
/// <c>altruist:server:drain:timeout</c> (30 s), <c>altruist:server:drain:force-grace</c> (5 s).
/// Publishes the <c>Altruist.Server</c> meter: <c>altruist.server.load</c>, <c>.max_load</c>,
/// <c>.state</c>, <c>.accepting</c>, <c>.units</c> and <c>.kind_load</c> (tagged <c>kind</c>).
/// </summary>
[Service(typeof(IServerNode))]
[ConditionalOnMissingService(typeof(IServerNode))]
public sealed class ServerNode : IServerNode, IDisposable
{
    public const string MeterName = "Altruist.Server";

    private readonly Lazy<IEnumerable<ICapacityContributor>> _staticContributors;
    private readonly Lazy<IEnumerable<IDrainParticipant>> _staticParticipants;
    private readonly Func<ReadyState>? _readiness;
    private readonly ILogger _logger;
    private readonly Meter _meter;
    private readonly object _gate = new();

    private ICapacityContributor[] _contributors = Array.Empty<ICapacityContributor>();
    private IDrainParticipant[] _participants = Array.Empty<IDrainParticipant>();
    private Task<bool>? _drain;
    private volatile bool _draining;
    private volatile bool _drained;
    private volatile bool _forced;
    private long _lastFaultLog;

    [ActivatorUtilitiesConstructor]
    public ServerNode(
        Lazy<IEnumerable<ICapacityContributor>> contributors,
        Lazy<IEnumerable<IDrainParticipant>> participants,
        [AppConfigValue("altruist:server:capacity:max-load", "0")] double maxLoad,
        [AppConfigValue("altruist:server:drain:timeout", "30")] double drainTimeoutSeconds,
        [AppConfigValue("altruist:server:drain:force-grace", "5")] double forceGraceSeconds,
        IServerStatus? status = null,
        IAltruistContext? context = null,
        ILoggerFactory? loggerFactory = null)
        : this(contributors, participants, new ServerNodeOptions
        {
            MaxLoad = maxLoad,
            DrainTimeout = TimeSpan.FromSeconds(drainTimeoutSeconds),
            ForceStopGrace = TimeSpan.FromSeconds(forceGraceSeconds),
        }, status is null ? null : () => status.Status, context?.ProcessId, loggerFactory)
    {
    }

    /// <summary>Manual constructor (tests, custom hosts). <paramref name="readiness"/> null: always started.</summary>
    public ServerNode(
        ServerNodeOptions? options = null,
        IEnumerable<ICapacityContributor>? contributors = null,
        IEnumerable<IDrainParticipant>? participants = null,
        Func<ReadyState>? readiness = null,
        string? nodeId = null,
        ILoggerFactory? loggerFactory = null)
        : this(new Lazy<IEnumerable<ICapacityContributor>>(() => contributors ?? Array.Empty<ICapacityContributor>()),
            new Lazy<IEnumerable<IDrainParticipant>>(() => participants ?? Array.Empty<IDrainParticipant>()),
            options ?? new ServerNodeOptions(), readiness, nodeId, loggerFactory)
    {
    }

    private ServerNode(
        Lazy<IEnumerable<ICapacityContributor>> contributors,
        Lazy<IEnumerable<IDrainParticipant>> participants,
        ServerNodeOptions options,
        Func<ReadyState>? readiness,
        string? nodeId,
        ILoggerFactory? loggerFactory)
    {
        if (options.MaxLoad < 0 || double.IsNaN(options.MaxLoad))
            throw new ArgumentOutOfRangeException(nameof(options), options.MaxLoad, "max-load must be 0 (unlimited) or positive.");
        if (options.DrainTimeout < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(options), options.DrainTimeout, "drain timeout must not be negative.");
        _staticContributors = contributors;
        _staticParticipants = participants;
        Options = options;
        _readiness = readiness;
        NodeId = string.IsNullOrEmpty(nodeId) ? $"{Environment.MachineName}-{Environment.ProcessId}" : nodeId;
        _logger = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ServerNode>();
        _meter = new Meter(MeterName);
        CreateInstruments();
    }

    public ServerNodeOptions Options { get; }

    /// <summary>This node's <c>Altruist.Server</c> meter (one per node instance).</summary>
    public Meter Metrics => _meter;
    public string NodeId { get; }
    public double MaxLoad => Options.MaxLoad;
    public TimeSpan DrainTimeout => Options.DrainTimeout;
    public bool IsDraining => _draining;

    /// <summary>The last drain stopped work that had not finished in time.</summary>
    public bool WasForced => _forced;

    public event Action? DrainStarted;

    public ServerNodeState State => StateFor(Load(out _));

    // ------------------------------------------------------------------ capacity

    public ServerCapacity Capacity()
    {
        var total = Load(out var kinds);
        return new ServerCapacity(NodeId, StateFor(total), total, MaxLoad, kinds);
    }

    public bool CanAccept(double cost = 1)
    {
        if (cost < 0 || double.IsNaN(cost))
            throw new ArgumentOutOfRangeException(nameof(cost), cost, "cost must not be negative.");
        var load = Load(out _);
        if (StateFor(load) != ServerNodeState.Ready)
            return false;
        return MaxLoad <= 0 || load + cost <= MaxLoad + 1e-9;
    }

    private ServerNodeState StateFor(double load)
    {
        if (_drained) return ServerNodeState.Drained;
        if (_draining) return ServerNodeState.Draining;
        if (_readiness is not null && _readiness() != ReadyState.Alive) return ServerNodeState.Starting;
        if (MaxLoad > 0 && load >= MaxLoad - 1e-9) return ServerNodeState.Full;
        return ServerNodeState.Ready;
    }

    private IEnumerable<ICapacityContributor> AllContributors()
    {
        foreach (var c in _staticContributors.Value ?? Array.Empty<ICapacityContributor>())
            if (c is not null) yield return c;
        foreach (var c in Volatile.Read(ref _contributors))
            yield return c;
    }

    private double Load(out IReadOnlyList<CapacityByKind> kinds)
    {
        var byKind = new Dictionary<string, (int Units, double Load)>(StringComparer.Ordinal);
        var seen = new HashSet<ICapacityContributor>(ReferenceEqualityComparer.Instance);
        double total = 0;
        foreach (var c in AllContributors())
        {
            if (!seen.Add(c)) continue;
            CapacitySample s;
            try
            {
                s = c.Sample();
            }
            catch (Exception ex)
            {
                // A broken contributor must not take the report (or readiness) down with it.
                LogFault(ex, "capacity contributor {Kind} threw; counted as empty", c.Kind);
                s = CapacitySample.Empty;
            }
            var load = double.IsFinite(s.Load) && s.Load > 0 ? s.Load : 0;
            var units = Math.Max(0, s.Units);
            var kind = c.Kind ?? "";
            byKind[kind] = byKind.TryGetValue(kind, out var prev) ? (prev.Units + units, prev.Load + load) : (units, load);
            total += load;
        }
        kinds = byKind.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new CapacityByKind(k.Key, k.Value.Units, k.Value.Load)).ToList();
        return total;
    }

    public IDisposable Register(ICapacityContributor contributor)
    {
        ArgumentNullException.ThrowIfNull(contributor);
        lock (_gate)
            _contributors = _contributors.Append(contributor).ToArray();
        return new Registration(() =>
        {
            lock (_gate)
                _contributors = _contributors.Where(c => !ReferenceEquals(c, contributor)).ToArray();
        });
    }

    public IDisposable Register(IDrainParticipant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        bool begin, force;
        lock (_gate)
        {
            _participants = _participants.Append(participant).ToArray();
            begin = _draining;
            force = _forced;
        }
        // Joined a drain already under way: it winds down right away.
        if (begin) SafeCall(participant, "BeginDrain", static p => p.BeginDrain());
        if (force) SafeCall(participant, "ForceStop", static p => p.ForceStop());
        return new Registration(() =>
        {
            lock (_gate)
                _participants = _participants.Where(p => !ReferenceEquals(p, participant)).ToArray();
        });
    }

    // ------------------------------------------------------------------ drain

    public Task<bool> DrainAsync(TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        Task<bool> drain;
        TaskCompletionSource<bool>? result = null;
        IDrainParticipant[] participants;
        lock (_gate)
        {
            if (_drain is null)
            {
                _draining = true;
                result = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _drain = result.Task;
                participants = Participants();
            }
            else participants = Array.Empty<IDrainParticipant>();
            drain = _drain;
        }
        if (result is not null)
        {
            var budget = timeout ?? DrainTimeout;
            _logger.LogInformation("Server {Node} draining: no new work, waiting up to {Timeout} for the running work.", NodeId, budget);
            // Every participant knows before the first check.
            foreach (var p in participants) SafeCall(p, "BeginDrain", static x => x.BeginDrain());
            try
            { DrainStarted?.Invoke(); }
            catch (Exception ex) { LogFault(ex, "a DrainStarted handler threw", ""); }
            _ = CompleteAsync(result, budget);
        }
        return cancellationToken.CanBeCanceled ? drain.WaitAsync(cancellationToken) : drain;
    }

    private async Task CompleteAsync(TaskCompletionSource<bool> result, TimeSpan timeout)
    {
        try
        { result.TrySetResult(await RunDrainAsync(timeout).ConfigureAwait(false)); }
        catch (Exception ex)
        {
            _drained = true;
            result.TrySetException(ex);
        }
    }

    private IDrainParticipant[] Participants()
    {
        var all = new List<IDrainParticipant>();
        var seen = new HashSet<IDrainParticipant>(ReferenceEqualityComparer.Instance);
        foreach (var p in _staticParticipants.Value ?? Array.Empty<IDrainParticipant>())
            if (p is not null && seen.Add(p)) all.Add(p);
        foreach (var p in _participants)
            if (seen.Add(p)) all.Add(p);
        return all.ToArray();
    }

    private async Task<bool> RunDrainAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            if (AllDrained())
            {
                _drained = true;
                _logger.LogInformation("Server {Node} drained in {Elapsed}.", NodeId, sw.Elapsed);
                return true;
            }
            if (sw.Elapsed >= timeout) break;
            await Task.Delay(Options.PollInterval).ConfigureAwait(false);
        }

        IDrainParticipant[] left;
        lock (_gate)
        {
            _forced = true;
            left = Participants();
        }
        var stopped = left.Where(p => !IsDrained(p)).ToList();
        _logger.LogWarning("Server {Node} drain timed out after {Timeout}; stopping {Kinds}.", NodeId, timeout,
            string.Join(", ", stopped.Select(p => p.Kind)));
        foreach (var p in stopped) SafeCall(p, "ForceStop", static x => x.ForceStop());

        var grace = Stopwatch.StartNew();
        while (!AllDrained() && grace.Elapsed < Options.ForceStopGrace)
            await Task.Delay(Options.PollInterval).ConfigureAwait(false);
        _drained = true;
        return false;
    }

    private bool AllDrained()
    {
        IDrainParticipant[] all;
        lock (_gate)
            all = Participants();
        return all.All(IsDrained);
    }

    private bool IsDrained(IDrainParticipant p)
    {
        try
        {
            return p.IsDrained;
        }
        catch (Exception ex)
        {
            // Cannot tell: do not hold the process hostage.
            LogFault(ex, "drain participant {Kind}.IsDrained threw; treated as drained", p.Kind);
            return true;
        }
    }

    private void SafeCall(IDrainParticipant p, string what, Action<IDrainParticipant> call)
    {
        try
        { call(p); }
        catch (Exception ex) { LogFault(ex, $"drain participant {{Kind}}.{what} threw", p.Kind); }
    }

    private void LogFault(Exception ex, string message, string kind)
    {
        var now = Stopwatch.GetTimestamp();
        var last = Interlocked.Read(ref _lastFaultLog);
        if (last != 0 && now - last < Stopwatch.Frequency * 10) return;
        Interlocked.Exchange(ref _lastFaultLog, now);
        _logger.LogError(ex, message, kind);
    }

    // ------------------------------------------------------------------ metrics

    private void CreateInstruments()
    {
        _meter.CreateObservableGauge("altruist.server.load", () => Load(out _), description: "Summed load of the server's work.");
        _meter.CreateObservableGauge("altruist.server.max_load", () => MaxLoad, description: "Configured load budget (0 = unlimited).");
        _meter.CreateObservableGauge("altruist.server.state", () => (int)State, description: "0 starting, 1 ready, 2 full, 3 draining, 4 drained.");
        _meter.CreateObservableGauge("altruist.server.accepting", () => State == ServerNodeState.Ready ? 1 : 0, description: "1 when the server takes new work.");
        _meter.CreateObservableGauge("altruist.server.units", () => Measure(k => k.Units), description: "Live units per kind of work.");
        _meter.CreateObservableGauge("altruist.server.kind_load", () => Measure(k => k.Load), description: "Load per kind of work.");
    }

    private IEnumerable<Measurement<double>> Measure(Func<CapacityByKind, double> value)
    {
        Load(out var kinds);
        foreach (var k in kinds)
            yield return new Measurement<double>(value(k), new KeyValuePair<string, object?>("kind", k.Kind));
    }

    public void Dispose() => _meter.Dispose();

    private sealed class Registration(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
