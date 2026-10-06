/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Diagnostics.Metrics;

using Altruist.Gaming;

namespace Tests.Gaming.Engine;

/// <summary>
/// Tests that keep every core busy (parallel physics, parallel rooms) run on their own, so the
/// wall-clock engine tests elsewhere in the suite are not starved by them.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CpuHeavyCollection
{
    public const string Name = "cpu-heavy";
}

[Collection(CpuHeavyCollection.Name)]
public class StepSchedulerTests
{
    [Theory]
    [InlineData("1", 1)]
    [InlineData("4", 4)]
    [InlineData(" 3 ", 3)]
    public void Workers_parse_from_config(string value, int expected) =>
        Assert.Equal(expected, new StepScheduler(value).Workers);

    [Theory]
    [InlineData("auto")]
    [InlineData("AUTO")]
    [InlineData("")]
    [InlineData(null)]
    public void Auto_means_one_worker_per_core(string? value) =>
        Assert.Equal(Math.Max(1, Environment.ProcessorCount), new StepScheduler(value).Workers);

    [Theory]
    [InlineData("0")]
    [InlineData("-2")]
    [InlineData("many")]
    public void Nonsense_worker_counts_are_refused(string value) =>
        Assert.Throws<ArgumentException>(() => new StepScheduler(value));

    [Fact]
    public void Zero_workers_are_refused() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new StepScheduler(0));

    [Fact]
    public void Inline_runs_in_order_on_the_calling_thread()
    {
        var thread = Environment.CurrentManagedThreadId;
        var order = new List<int>();
        StepScheduler.Inline.ForEach(Enumerable.Range(0, 50).ToList(), i =>
        {
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
            order.Add(i);
        });
        Assert.Equal(Enumerable.Range(0, 50), order);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Every_item_runs_exactly_once(int workers)
    {
        var counts = new int[500];
        new StepScheduler(workers).ForEach(Enumerable.Range(0, counts.Length).ToList(), i => Interlocked.Increment(ref counts[i]));
        Assert.All(counts, c => Assert.Equal(1, c));
    }

    [Fact]
    public void Several_workers_really_run_at_the_same_time()
    {
        // Four items that only finish once all four are running: needs four threads at once.
        using var barrier = new Barrier(4);
        var threads = new ConcurrentDictionary<int, bool>();
        new StepScheduler(4).ForEach(Enumerable.Range(0, 4).ToList(), _ =>
        {
            threads[Environment.CurrentManagedThreadId] = true;
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)), "the items did not run in parallel");
        });
        Assert.Equal(4, threads.Count);
    }

    [Fact]
    public void The_worker_count_caps_the_parallelism()
    {
        var running = 0;
        var peak = 0;
        new StepScheduler(2).ForEach(Enumerable.Range(0, 40).ToList(), _ =>
        {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Thread.Sleep(2);
            Interlocked.Decrement(ref running);
        });
        Assert.InRange(peak, 1, 2);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void Failures_are_collected_after_every_item_ran(int workers)
    {
        var ran = new ConcurrentBag<int>();
        var ex = Assert.Throws<AggregateException>(() => new StepScheduler(workers).ForEach(Enumerable.Range(0, 20).ToList(), i =>
        {
            ran.Add(i);
            if (i % 5 == 0) throw new InvalidOperationException($"item {i}");
        }));
        Assert.Equal(20, ran.Count);
        Assert.Equal(new[] { "item 0", "item 10", "item 15", "item 5" }, ex.InnerExceptions.Select(e => e.Message).OrderBy(m => m, StringComparer.Ordinal));
    }

    [Fact]
    public void Empty_and_single_item_lists_need_no_threads()
    {
        var scheduler = new StepScheduler(8);
        scheduler.ForEach(Array.Empty<int>(), _ => throw new InvalidOperationException());
        var thread = Environment.CurrentManagedThreadId;
        var ran = false;
        scheduler.ForEach(new[] { 1 }, _ =>
        {
            ran = true;
            Assert.Equal(thread, Environment.CurrentManagedThreadId);
        });
        Assert.True(ran);
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while (value > (seen = Volatile.Read(ref target)) && Interlocked.CompareExchange(ref target, value, seen) != seen) { }
    }
}

/// <summary>A stepper with a name unique to these tests, so other tests' coordinators cannot pollute the counts.</summary>
public sealed class MeteredOverrunStepper : IWorldStepper
{
    public StepMode Mode => StepMode.Fixed;
    public int FixedHz => 60;
    public void FixedStep(in FixedStep step) { }
}

public sealed class MeteredVariableStepper : IWorldStepper
{
    public void Step(float dt) => Thread.Sleep(1);
}

public class WorldCoordinatorMetricsTests
{
    [Fact]
    public void Frames_steppers_fixed_steps_and_overruns_are_measured()
    {
        var durations = new ConcurrentBag<(string Name, string? Stepper, double Ms)>();
        var counters = new ConcurrentDictionary<string, long>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == WorldCoordinator.MeterName) l.EnableMeasurementEvents(instrument);
        };
        static string? StepperOf(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var t in tags)
                if (t.Key == "stepper") return t.Value as string;
            return null;
        }
        listener.SetMeasurementEventCallback<double>((i, v, tags, _) => durations.Add((i.Name, StepperOf(tags), v)));
        listener.SetMeasurementEventCallback<long>((i, v, tags, _) =>
        {
            if (StepperOf(tags) is { } s && s.StartsWith("Metered", StringComparison.Ordinal))
                counters.AddOrUpdate($"{i.Name}:{s}", v, (_, old) => old + v);
        });
        listener.Start();

        var world = new WorldCoordinator(new IWorldStepper[] { new MeteredOverrunStepper(), new MeteredVariableStepper() }, maxFrameDelta: 1, maxStepsPerFrame: 4);
        world.Step(1f / 60f);     // 1 fixed step
        world.Step(0.5f);         // 30 due, capped at 4: an overrun

        Assert.Equal(5, counters["altruist.engine.fixed_steps:MeteredOverrunStepper"]);
        Assert.Equal(1, counters["altruist.engine.overruns:MeteredOverrunStepper"]);
        Assert.False(counters.ContainsKey("altruist.engine.fixed_steps:MeteredVariableStepper"));
        var variable = durations.Where(d => d.Name == "altruist.engine.stepper.duration" && d.Stepper == "MeteredVariableStepper").ToList();
        Assert.Equal(2, variable.Count);
        Assert.All(variable, d => Assert.True(d.Ms >= 0.5, $"{d.Ms} ms"));
        Assert.Contains(durations, d => d.Name == "altruist.engine.frame.duration" && d.Ms >= 1);
    }
}
