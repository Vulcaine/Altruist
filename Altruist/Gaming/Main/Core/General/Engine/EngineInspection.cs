/*
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
You may not use this file except in compliance with the License.
You may obtain a copy at http://www.apache.org/licenses/LICENSE-2.0
*/

using System.Diagnostics;

namespace Altruist.Engine;

/// <summary>
/// What an operator sees of the engine (the dashboard reads it): frame timing, every <c>[Cycle]</c> task and cron job
/// with its runs and failures, and the failures by source. <see cref="AltruistEngine"/> implements it; every member may
/// be read from any thread and returns a copy.
/// </summary>
public interface IEngineInspection
{
    /// <summary>Engine frames run so far.</summary>
    long Frame { get; }

    /// <summary>Whether the engine loop runs.</summary>
    bool Enabled { get; }

    /// <summary>Frame timing over the last frames.</summary>
    EngineFrameReport Frames();

    /// <summary>Every <c>[Cycle]</c> task (and task scheduled with a rate).</summary>
    IReadOnlyList<EngineTaskReport> CycleTasks();

    /// <summary>Every cron job.</summary>
    IReadOnlyList<CronJobReport> CronJobs();

    /// <summary>Failures by source (task, cron job, world step …), most recent first.</summary>
    IReadOnlyList<EngineFaultReport> Faults();
}

/// <summary>Frame timing over the last frames.</summary>
/// <param name="TargetHz">The configured rate.</param>
/// <param name="FramesPerSecond">Frames started in the last second.</param>
/// <param name="LastMs">Work time of the last frame.</param>
/// <param name="AverageMs">Mean work time over the window.</param>
/// <param name="P95Ms">95th percentile work time over the window.</param>
/// <param name="MaxMs">Longest work time over the window.</param>
/// <param name="OverBudget">Frames in the window that took longer than one period (1 / <paramref name="TargetHz"/>).</param>
/// <param name="Window">Frames the figures cover.</param>
public sealed record EngineFrameReport(double TargetHz, double FramesPerSecond, double LastMs, double AverageMs, double P95Ms, double MaxMs, int OverBudget, int Window);

/// <summary>One <c>[Cycle]</c> task.</summary>
public sealed record EngineTaskReport(string Name, string Rate, long Runs, long Faults, DateTime? LastRunUtc, double LastMs, double AverageMs, double MaxMs, bool Running, string? LastFault);

/// <summary>One cron job.</summary>
public sealed record CronJobReport(string Name, string Expression, DateTime? NextUtc, DateTime? LastRunUtc, long Runs, long Faults, double LastMs, bool Running, string? LastFault);

/// <summary>Failures of one source.</summary>
public sealed record EngineFaultReport(string Source, long Count, DateTime LastUtc, string LastMessage);

/// <summary>Runs, timing and failures of one task or job (thread-safe).</summary>
internal sealed class RunStats
{
    private readonly object _gate = new();
    private long _runs;
    private long _faults;
    private DateTime? _lastRunUtc;
    private double _lastMs;
    private double _totalMs;
    private double _maxMs;
    private int _running;
    private string? _lastFault;

    public void Started(DateTime utc)
    {
        lock (_gate)
        {
            _lastRunUtc = utc;
            _running++;
        }
    }

    public void Finished(double ms, Exception? fault)
    {
        lock (_gate)
        {
            _running = Math.Max(0, _running - 1);
            _runs++;
            _lastMs = ms;
            _totalMs += ms;
            _maxMs = Math.Max(_maxMs, ms);
            if (fault is not null)
            {
                _faults++;
                _lastFault = fault.GetBaseException().Message;
            }
        }
    }

    public EngineTaskReport TaskReport(string name, string rate)
    {
        lock (_gate)
            return new EngineTaskReport(name, rate, _runs, _faults, _lastRunUtc, _lastMs, _runs > 0 ? _totalMs / _runs : 0, _maxMs, _running > 0, _lastFault);
    }

    public CronJobReport CronReport(string name, string expression, DateTime? next)
    {
        lock (_gate)
            return new CronJobReport(name, expression, next, _lastRunUtc, _runs, _faults, _lastMs, _running > 0, _lastFault);
    }
}

/// <summary>The last frames' work times (thread-safe; written by the engine loop).</summary>
internal sealed class FrameStats
{
    private const int Size = 256;
    private readonly object _gate = new();
    private readonly double[] _ms = new double[Size];
    private readonly long[] _startedAt = new long[Size];
    private int _count;
    private int _next;
    private long _frameStart;
    private double _lastMs;

    public void Begin() => _frameStart = Stopwatch.GetTimestamp();

    public void End()
    {
        var ms = Stopwatch.GetElapsedTime(_frameStart).TotalMilliseconds;
        lock (_gate)
        {
            _lastMs = ms;
            _ms[_next] = ms;
            _startedAt[_next] = _frameStart;
            _next = (_next + 1) % Size;
            _count = Math.Min(Size, _count + 1);
        }
    }

    public EngineFrameReport Report(double targetHz)
    {
        double[] ms;
        long[] started;
        double last;
        lock (_gate)
        {
            ms = _ms[.._count];
            started = _startedAt[.._count];
            last = _lastMs;
        }
        if (ms.Length == 0)
            return new EngineFrameReport(targetHz, 0, 0, 0, 0, 0, 0, 0);

        var now = Stopwatch.GetTimestamp();
        var perSecond = started.Count(t => Stopwatch.GetElapsedTime(t, now) <= TimeSpan.FromSeconds(1));
        var sorted = ms.Order().ToArray();
        var budget = targetHz > 0 ? 1000.0 / targetHz : double.MaxValue;
        return new EngineFrameReport(
            targetHz,
            perSecond,
            last,
            ms.Average(),
            sorted[(int)Math.Min(sorted.Length - 1, Math.Floor(sorted.Length * 0.95))],
            sorted[^1],
            ms.Count(x => x > budget),
            ms.Length);
    }
}
