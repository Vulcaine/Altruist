/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Diagnostics;

namespace Altruist.Engine;

/// <summary>
/// The time source of the engine: frame times, time-based <c>[Cycle]</c> rates, effects and
/// <c>ScheduleOnce</c> deadlines. Ticks are in <see cref="Stopwatch.Frequency"/> units.
/// The default is the <see cref="Stopwatch"/>; tests register a <see cref="ManualEngineClock"/>.
/// </summary>
public interface IEngineClock
{
    long NowTicks { get; }
}

/// <summary>The real clock (<see cref="Stopwatch.GetTimestamp"/>).</summary>
public sealed class StopwatchEngineClock : IEngineClock
{
    public static readonly StopwatchEngineClock Instance = new();
    public long NowTicks => Stopwatch.GetTimestamp();
}

/// <summary>A clock that only moves when told to; drive the engine with <see cref="EngineTestDriver"/>.</summary>
public sealed class ManualEngineClock : IEngineClock
{
    private long _now;

    public ManualEngineClock(long startTicks = 1) => _now = startTicks;

    public long NowTicks => Interlocked.Read(ref _now);

    public void Advance(TimeSpan by) => Advance(by.TotalSeconds);

    public void Advance(double seconds)
    {
        if (seconds < 0)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, "A clock does not go backwards.");
        Interlocked.Add(ref _now, (long)Math.Round(seconds * Stopwatch.Frequency));
    }
}
