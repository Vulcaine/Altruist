/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Diagnostics;

using Altruist;
using Altruist.Engine;
using Altruist.Gaming;

using Microsoft.Extensions.DependencyInjection;

using Moq;

namespace Tests.Gaming.Engine;

/// <summary>A wall clock that only moves when the test advances it; fires the timers that fall due.</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = new();
    private DateTimeOffset _now;

    public ManualTimeProvider(DateTimeOffset start) => _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
            return _now;
    }

    public int PendingTimers
    {
        get
        {
            lock (_gate)
                return _timers.Count(t => t.DueAt is not null);
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan by)
    {
        List<ManualTimer> due;
        lock (_gate)
        {
            _now += by;
            due = _timers.Where(t => t.DueAt is { } at && at <= _now).ToList();
            foreach (var t in due)
                t.DueAt = null;
        }
        foreach (var t in due)
            t.Fire();
    }

    private sealed class ManualTimer : ITimer
    {
        private readonly ManualTimeProvider _owner;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        public DateTimeOffset? DueAt;

        public ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state)
        {
            _owner = owner;
            _callback = callback;
            _state = state;
            lock (owner._gate)
                owner._timers.Add(this);
        }

        public void Fire() => _callback(_state);

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (_owner._gate)
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : _owner._now + dueTime;
            return true;
        }

        public void Dispose()
        {
            lock (_owner._gate)
                _owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

/// <summary>
/// Cron jobs (#120, #135): they used to run from an <c>async void</c> loop with <c>DynamicInvoke</c>, so one throwing
/// job crashed the process, a returned task was never awaited, and a cron more than ~49 days away (monthly, yearly)
/// made <c>Task.Delay</c> throw on the thread pool. Static <c>[Cycle]</c> methods failed startup.
/// </summary>
public sealed class EngineCronJobTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 30, TimeSpan.Zero);

    private static AltruistEngine NewEngine(TimeProvider time, IServiceProvider? services = null) =>
        new(new Mock<IServerStatus>().Object, services ?? new ServiceCollection().BuildServiceProvider(),
            new Mock<IGameWorldOrganizer>().Object, timeProvider: time);

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < Wait, $"timed out waiting for: {what}");
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void A_cron_job_runs_at_each_occurrence()
    {
        var time = new ManualTimeProvider(Start);
        using var engine = NewEngine(time);
        var runs = 0;

        engine.RegisterCronJob(new Action(() => Interlocked.Increment(ref runs)), CronPresets.EveryMinute);

        for (var i = 1; i <= 3; i++)
        {
            WaitUntil(() => time.PendingTimers == 1, "the job to wait for its next occurrence");
            time.Advance(TimeSpan.FromMinutes(1));
            var expected = i;
            WaitUntil(() => Volatile.Read(ref runs) == expected, $"run {expected}");
        }
    }

    [Fact]
    public void A_throwing_cron_job_runs_again_at_its_next_occurrence()
    {
        var time = new ManualTimeProvider(Start);
        using var engine = NewEngine(time);
        var runs = 0;

        engine.RegisterCronJob(new Action(() =>
        {
            Interlocked.Increment(ref runs);
            throw new InvalidOperationException("cron boom");
        }), CronPresets.EveryMinute);
        engine.RegisterCronJob(new Func<Task>(() => Task.FromException(new InvalidOperationException("async cron boom"))),
            CronPresets.EveryMinute);

        WaitUntil(() => time.PendingTimers == 2, "both jobs to wait");
        time.Advance(TimeSpan.FromMinutes(1));
        WaitUntil(() => Volatile.Read(ref runs) == 1 && time.PendingTimers == 2, "the first run");
        time.Advance(TimeSpan.FromMinutes(1));
        WaitUntil(() => Volatile.Read(ref runs) == 2, "the run after the exception");
    }

    [Fact]
    public void An_async_cron_job_is_awaited_and_never_overlaps_itself()
    {
        var time = new ManualTimeProvider(Start);
        using var engine = NewEngine(time);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = 0;

        engine.RegisterCronJob(new Func<Task>(() =>
        {
            Interlocked.Increment(ref started);
            return release.Task;
        }), CronPresets.EveryMinute);

        WaitUntil(() => time.PendingTimers == 1, "the job to wait");
        time.Advance(TimeSpan.FromMinutes(1));
        WaitUntil(() => Volatile.Read(ref started) == 1, "the first run");

        // The job is still running: further occurrences pass without a second, overlapping run.
        time.Advance(TimeSpan.FromMinutes(5));
        Thread.Sleep(50);
        Assert.Equal(0, time.PendingTimers);
        Assert.Equal(1, Volatile.Read(ref started));

        release.SetResult();
        WaitUntil(() => time.PendingTimers == 1, "the next occurrence after the run completed");
    }

    [Fact]
    public void A_yearly_cron_job_waits_longer_than_the_task_delay_limit()
    {
        var time = new ManualTimeProvider(Start);
        using var engine = NewEngine(time);
        var runs = 0;

        engine.RegisterCronJob(new Action(() => Interlocked.Increment(ref runs)), CronPresets.Yearly);

        for (var day = 0; day < 365 && Volatile.Read(ref runs) == 0; day++)
        {
            WaitUntil(() => time.PendingTimers == 1 || Volatile.Read(ref runs) > 0, "the job to wait");
            time.Advance(TimeSpan.FromDays(1));
        }

        WaitUntil(() => Volatile.Read(ref runs) == 1, "the yearly run");
        Assert.True(time.GetUtcNow() >= new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Disposing_the_engine_cancels_its_cron_jobs()
    {
        var time = new ManualTimeProvider(Start);
        var engine = NewEngine(time);
        var runs = 0;
        engine.RegisterCronJob(new Action(() => Interlocked.Increment(ref runs)), CronPresets.EveryMinute);
        WaitUntil(() => time.PendingTimers == 1, "the job to wait");

        engine.Dispose();

        WaitUntil(() => time.PendingTimers == 0, "the wait to be cancelled");
        time.Advance(TimeSpan.FromMinutes(1));
        Thread.Sleep(50);
        Assert.Equal(0, Volatile.Read(ref runs));
        Assert.Throws<ObjectDisposedException>(() =>
            engine.RegisterCronJob(new Action(() => { }), CronPresets.EveryMinute));
    }

    public sealed class StaticCycleService
    {
        public static int Runs;

        [Cycle(1)]
        public static void Tick() => Interlocked.Increment(ref Runs);
    }

    [Fact]
    public void A_static_cycle_method_is_scheduled()
    {
        var engine = new Mock<IEngineCore>();
        var scheduled = new List<Delegate>();
        engine.Setup(e => e.ScheduleTask(It.IsAny<Delegate>(), It.IsAny<CycleRate?>()))
            .Callback<Delegate, CycleRate?>((d, _) => scheduled.Add(d));
        var services = new ServiceCollection();
        services.AddSingleton<StaticCycleService>();
        using var provider = services.BuildServiceProvider();

        var methods = new MethodScheduler(engine.Object, provider).RegisterMethods(provider);

        Assert.Contains(methods, m => m.Name == nameof(StaticCycleService.Tick));
        var before = Volatile.Read(ref StaticCycleService.Runs);
        ((Func<Task>)Assert.Single(scheduled))().GetAwaiter().GetResult();
        Assert.Equal(before + 1, Volatile.Read(ref StaticCycleService.Runs));
    }
}
