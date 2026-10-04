/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Diagnostics;

using Altruist;
using Altruist.Engine;
using Altruist.Gaming;
using Altruist.Gaming.Engine;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Tests.Gaming.Engine;

// ------------------------------------------------------------------ fixtures

public sealed class EngineBootCounters
{
    public int CycleServiceConstructed;
    public int OrganizerConstructed;
    public int CycleRuns;
    public int CycleRunsOnUnconstructedInstance;
    public readonly ConcurrentDictionary<object, byte> CycleInstances = new(ReferenceEqualityComparer.Instance);
}

/// <summary>A user service with a [PostConstruct] hook and a [Cycle] method.</summary>
public sealed class CountingCycleService
{
    private readonly EngineBootCounters _counters;
    private bool _postConstructed;

    public CountingCycleService(EngineBootCounters counters)
    {
        _counters = counters;
        Interlocked.Increment(ref counters.CycleServiceConstructed);
    }

    [PostConstruct]
    public void Init() => _postConstructed = true;

    [Cycle(1)]
    public void Tick()
    {
        _counters.CycleInstances.TryAdd(this, 0);
        if (!_postConstructed)
            Interlocked.Increment(ref _counters.CycleRunsOnUnconstructedInstance);
        Interlocked.Increment(ref _counters.CycleRuns);
    }
}

/// <summary>The engine's only mandatory dependency besides the status: one per engine instance.</summary>
public sealed class CountingOrganizer : IGameWorldOrganizer
{
    public CountingOrganizer(EngineBootCounters counters) => Interlocked.Increment(ref counters.OrganizerConstructed);
    public void Step(float deltaTime) { }
}

/// <summary>Records every world step's dt; can stall to simulate a worker that falls behind.</summary>
public sealed class RecordingOrganizer : IGameWorldOrganizer
{
    private readonly int _stallMs;
    private double _total;
    public int Steps;

    public RecordingOrganizer(int stallMs = 0) => _stallMs = stallMs;

    public double TotalDt => Volatile.Read(ref _total);

    /// <summary>Wall time between the first and the latest step (seconds) and the dt of the first step.</summary>
    public double FirstStepAt = -1, LastStepAt, FirstDt;

    public void Step(float deltaTime)
    {
        var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
        if (FirstStepAt < 0)
        {
            FirstStepAt = now;
            FirstDt = deltaTime;
        }
        LastStepAt = now;
        Interlocked.Increment(ref Steps);
        double seen, next;
        do
        {
            seen = Volatile.Read(ref _total);
            next = seen + deltaTime;
        } while (Interlocked.CompareExchange(ref _total, next, seen) != seen);
        if (_stallMs > 0)
            Thread.Sleep(_stallMs);
    }
}

// ------------------------------------------------------------------ tests

/// <summary>
/// Engine lifecycle and loop resilience (0.9.7):
/// <list type="bullet">
/// <item>EngineStartupConfiguration used to build a throwaway service provider, construct every
/// singleton in it and start a second engine on it: services ran twice, [Cycle] could land on the
/// wrong instance and an orphan engine thread slept forever.</item>
/// <item>One throwing WaitForNextTick delegate stopped the whole game loop.</item>
/// <item>EngineWithoutDiagnostics.SyncCommit threw NotImplementedException.</item>
/// <item>CycleUnit.Seconds rates were read as seconds per cycle instead of cycles per second.</item>
/// <item>The physics channel dropped the dt of frames the world worker missed.</item>
/// </list>
/// Tests in this class start real engines; xUnit runs them one after another.
/// </summary>
public sealed class EngineLifecycleRegressionTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);
    private static readonly CycleRate EveryFrame = new(1, CycleUnit.Ticks);

    private static IServerStatus AliveStatus()
    {
        var status = new Mock<IServerStatus>();
        status.SetupGet(s => s.Status).Returns(ReadyState.Alive);
        return status.Object;
    }

    private static AltruistEngine NewEngine(IGameWorldOrganizer? organizer = null, int hz = 200, IServiceProvider? services = null) =>
        new(AliveStatus(), services ?? new ServiceCollection().BuildServiceProvider(),
            organizer ?? new Mock<IGameWorldOrganizer>().Object, framerateHz: hz);

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var sw = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(sw.Elapsed < Wait, $"timed out waiting for: {what}");
            Thread.Sleep(5);
        }
    }

    // ------------------------------------------------------------------ boot

    [Fact]
    public async Task Configure_builds_no_provider_and_starts_no_engine()
    {
        var counters = new EngineBootCounters();
        var engine = new Mock<IEngineCore>();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(counters);
        services.AddSingleton<CountingCycleService>();
        services.AddSingleton<IGameWorldOrganizer, CountingOrganizer>();
        services.AddSingleton(engine.Object);
        services.AddSingleton<MethodScheduler>();

        await new EngineStartupConfiguration().Configure(services);

        Assert.Equal(0, counters.CycleServiceConstructed);
        Assert.Equal(0, counters.OrganizerConstructed);
        engine.Verify(e => e.Start(It.IsAny<CancellationToken>()), Times.Never);
        engine.Verify(e => e.ScheduleTask(It.IsAny<Delegate>(), It.IsAny<CycleRate?>()), Times.Never);
    }

    /// <summary>
    /// The boot sequence: [ServiceConfiguration]s, the root provider, then every service resolved
    /// and its [PostConstruct] run in registration order (ServerStatus first here, so the engine is
    /// already running when MethodScheduler registers the [Cycle] methods).
    /// </summary>
    [Fact]
    public async Task One_boot_constructs_each_singleton_once_runs_cycles_on_it_and_starts_one_engine()
    {
        var counters = new EngineBootCounters();
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton(counters);
        services.AddSingleton<IServerStatus>(sp => new ServerStatus(Array.Empty<IConnectable>(),
            sp.GetRequiredService<ILoggerFactory>(), new Mock<IHostApplicationLifetime>().Object));
        services.AddSingleton<IGameWorldOrganizer, CountingOrganizer>();
        services.AddSingleton<IEngineCore>(sp => new AltruistEngine(sp.GetRequiredService<IServerStatus>(), sp,
            sp.GetRequiredService<IGameWorldOrganizer>(), framerateHz: 200));
        services.AddSingleton<IAltruistEngine>(sp => new EngineWithoutDiagnostics(sp.GetRequiredService<IEngineCore>()));
        services.AddSingleton<CountingCycleService>();
        services.AddSingleton<MethodScheduler>();

        await new EngineStartupConfiguration().Configure(services);

        await using var provider = services.BuildServiceProvider();
        var cfg = new ConfigurationBuilder().Build();
        foreach (var serviceType in services.Select(d => d.ServiceType).Where(t => !t.IsGenericTypeDefinition).Distinct().ToList())
        {
            var instance = provider.GetService(serviceType);
            if (instance is not null)
                await DependencyResolver.InvokePostConstructAsync(instance, provider, cfg, NullLogger.Instance);
        }

        var engine = provider.GetRequiredService<IEngineCore>();
        try
        {
            Assert.Equal(ReadyState.Alive, provider.GetRequiredService<IServerStatus>().Status);
            WaitUntil(() => Volatile.Read(ref counters.CycleRuns) >= 5, "the [Cycle] method to run");

            Assert.Equal(1, counters.CycleServiceConstructed);
            // The organizer is an engine dependency: one construction = one engine for the boot.
            Assert.Equal(1, counters.OrganizerConstructed);
            Assert.Single(counters.CycleInstances.Keys);
            Assert.Same(provider.GetRequiredService<CountingCycleService>(), counters.CycleInstances.Keys.Single());
            Assert.Equal(0, counters.CycleRunsOnUnconstructedInstance);
            Assert.True(engine.Enabled);
        }
        finally
        {
            engine.Stop();
        }
    }

    [Fact]
    public void ServerStatus_publishes_alive_before_it_starts_the_engine()
    {
        var status = new ServerStatus(Array.Empty<IConnectable>(), NullLoggerFactory.Instance, new Mock<IHostApplicationLifetime>().Object);
        ReadyState? seen = null;
        var engine = new Mock<IEngineCore>();
        engine.Setup(e => e.Start(It.IsAny<CancellationToken>())).Callback(() => seen = status.Status);

        status.SignalState(engine.Object, ReadyState.Alive, default);

        engine.Verify(e => e.Start(It.IsAny<CancellationToken>()), Times.Once);
        Assert.Equal(ReadyState.Alive, seen);
    }

    // ------------------------------------------------------------------ loop resilience

    [Fact]
    public void A_throwing_next_tick_delegate_does_not_stop_the_loop()
    {
        var engine = NewEngine();
        var cycles = 0;
        engine.ScheduleTask(new Action(() => Interlocked.Increment(ref cycles)), EveryFrame);
        engine.Start(default);
        try
        {
            var after = new ManualResetEventSlim();
            engine.WaitForNextTick(() => throw new InvalidOperationException("boom"));
            engine.WaitForNextTick(new Func<Task>(() => throw new InvalidOperationException("async boom")));
            engine.WaitForNextTick(() => after.Set());

            Assert.True(after.Wait(Wait), "the delegate queued after the throwing ones never ran");
            var before = Volatile.Read(ref cycles);
            WaitUntil(() => Volatile.Read(ref cycles) > before + 5, "frames after the exception");

            var later = new ManualResetEventSlim();
            engine.WaitForNextTick(() => later.Set());
            Assert.True(later.Wait(Wait), "next-tick queue stopped after an exception");
        }
        finally
        {
            engine.Stop();
        }
    }

    [Fact]
    public void A_cycle_task_that_throws_every_frame_does_not_stop_the_others()
    {
        var engine = NewEngine();
        var throws = 0;
        var healthy = 0;
        engine.ScheduleTask(new Action(() =>
        {
            Interlocked.Increment(ref throws);
            throw new InvalidOperationException("cycle boom");
        }), EveryFrame);
        engine.ScheduleTask(new Func<Task>(() => throw new InvalidOperationException("async cycle boom")), EveryFrame);
        engine.ScheduleTask(new Action(() => Interlocked.Increment(ref healthy)), EveryFrame);
        engine.Start(default);
        try
        {
            WaitUntil(() => Volatile.Read(ref throws) > 10 && Volatile.Read(ref healthy) > 10, "both tasks to keep running");
        }
        finally
        {
            engine.Stop();
        }
    }

    [Fact]
    public void An_async_cycle_task_never_overlaps_itself()
    {
        var engine = NewEngine();
        var running = 0;
        var maxConcurrent = 0;
        var runs = 0;
        engine.ScheduleTask(new Func<Task>(async () =>
        {
            var now = Interlocked.Increment(ref running);
            int seen;
            while ((seen = Volatile.Read(ref maxConcurrent)) < now && Interlocked.CompareExchange(ref maxConcurrent, now, seen) != seen) { }
            await Task.Delay(30);
            Interlocked.Decrement(ref running);
            Interlocked.Increment(ref runs);
        }), EveryFrame);
        engine.Start(default);
        try
        {
            WaitUntil(() => Volatile.Read(ref runs) >= 4, "the async task to run a few times");
            Assert.Equal(1, Volatile.Read(ref maxConcurrent));
        }
        finally
        {
            engine.Stop();
        }
    }

    [Fact]
    public void Tasks_registered_after_start_are_picked_up()
    {
        var engine = NewEngine();
        engine.Start(default);
        try
        {
            var runs = 0;
            var registrations = Enumerable.Range(0, 50).Select(_ => Task.Run(() =>
                engine.ScheduleTask(new Action(() => Interlocked.Increment(ref runs)), EveryFrame))).ToArray();
            Task.WaitAll(registrations);
            WaitUntil(() => Volatile.Read(ref runs) >= 50 * 3, "every late registration to run");
        }
        finally
        {
            engine.Stop();
        }
    }

    [Fact]
    public void The_engine_runs_again_after_stop_and_start()
    {
        var organizer = new RecordingOrganizer();
        var engine = NewEngine(organizer);
        var cycles = 0;
        engine.ScheduleTask(new Action(() => Interlocked.Increment(ref cycles)), EveryFrame);

        engine.Start(default);
        WaitUntil(() => Volatile.Read(ref cycles) > 3 && Volatile.Read(ref organizer.Steps) > 3, "the first run");
        engine.Stop();

        var cyclesBefore = Volatile.Read(ref cycles);
        var stepsBefore = Volatile.Read(ref organizer.Steps);
        engine.Start(default);
        try
        {
            WaitUntil(() => Volatile.Read(ref cycles) > cyclesBefore + 3, "cycles after the restart");
            WaitUntil(() => Volatile.Read(ref organizer.Steps) > stepsBefore + 3, "world steps after the restart");
        }
        finally
        {
            engine.Stop();
        }
    }

    // ------------------------------------------------------------------ SyncCommit

    /// <summary>
    /// SyncCommit runs on the game loop: between frames, never concurrently with a [Cycle] task
    /// (the loop's frame work may hop pool threads, but it is strictly sequential).
    /// </summary>
    [Fact]
    public async Task SyncCommit_without_diagnostics_runs_on_the_game_loop()
    {
        var core = NewEngine();
        IAltruistEngine engine = new EngineWithoutDiagnostics(core);
        var inCycle = 0;
        var cycles = 0;
        engine.ScheduleTask(new Action(() =>
        {
            Interlocked.Exchange(ref inCycle, 1);
            Thread.SpinWait(2000);
            Interlocked.Increment(ref cycles);
            Interlocked.Exchange(ref inCycle, 0);
        }), EveryFrame);
        engine.Start(default);
        try
        {
            for (var i = 0; i < 20; i++)
            {
                var (overlapped, seenCycles) = await engine.SyncCommit(() => (Volatile.Read(ref inCycle) == 1, Volatile.Read(ref cycles))).WaitAsync(Wait);
                Assert.False(overlapped);
                Assert.True(i == 0 || seenCycles > 0);
            }

            var ran = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            engine.SyncCommit(() => ran.TrySetResult(Volatile.Read(ref inCycle) == 1));
            Assert.False(await ran.Task.WaitAsync(Wait));

            var failure = engine.SyncCommit<int>(() => throw new InvalidOperationException("query failed"));
            await Assert.ThrowsAsync<InvalidOperationException>(() => failure.WaitAsync(Wait));
        }
        finally
        {
            engine.Stop();
        }
    }

    // ------------------------------------------------------------------ cycle units

    [Fact]
    public void Seconds_and_milliseconds_rates_keep_their_frequency()
    {
        var perSecond = new CycleRate(30, CycleUnit.Seconds);
        Assert.Equal(30, perSecond.Frequency);
        Assert.Equal(TimeSpan.TicksPerSecond / 30, perSecond.Value);
        Assert.Equal("⚡ 30Hz", new CycleAttribute(30, CycleUnit.Seconds).ToString());
        Assert.Equal("⚡ every 2 frame(s)", new CycleAttribute(2).ToString());
    }

    [Fact]
    public void A_seconds_rate_runs_that_many_times_per_second()
    {
        var engine = NewEngine(hz: 200);
        var runs = 0;
        engine.ScheduleTask(new Action(() => Interlocked.Increment(ref runs)), new CycleRate(40, CycleUnit.Seconds));
        var effectSteps = 0;
        engine.ScheduleEffect(new CycleRate(40, CycleUnit.Seconds), DateTime.UtcNow.AddMinutes(1), _ => Interlocked.Increment(ref effectSteps));
        engine.Start(default);
        int taskRuns, effectRuns;
        try
        {
            WaitUntil(() => Volatile.Read(ref runs) >= 1 && Volatile.Read(ref effectSteps) >= 1, "the first runs");
            var runs0 = Volatile.Read(ref runs);
            var effects0 = Volatile.Read(ref effectSteps);
            Thread.Sleep(1000);
            taskRuns = Volatile.Read(ref runs) - runs0;
            effectRuns = Volatile.Read(ref effectSteps) - effects0;
        }
        finally
        {
            engine.Stop();
        }

        // 40 per second; before the fix the interval was 250,000 s and each ran once.
        // Loose lower bound: a loaded machine delays frames (and time-based tasks run once per frame).
        Assert.InRange(taskRuns, 10, 45);
        Assert.InRange(effectRuns, 10, 45);
    }

    // ------------------------------------------------------------------ world step dt

    [Fact]
    public void A_world_worker_that_falls_behind_still_steps_the_whole_elapsed_time()
    {
        // 200 Hz frames (5 ms), a world step that takes 25 ms: most frames happen while it runs.
        var organizer = new RecordingOrganizer(stallMs: 25);
        var engine = NewEngine(organizer, hz: 200);
        engine.Start(default);
        WaitUntil(() => Volatile.Read(ref organizer.Steps) >= 1, "the first world step");
        Thread.Sleep(1000);
        engine.Stop();
        Thread.Sleep(100);

        // The worker covered the wall time between its first and last step (plus the first dt).
        // Before the fix only the latest frame's dt reached the world: about 1/5 of that time.
        var covered = organizer.LastStepAt - organizer.FirstStepAt;
        Assert.True(organizer.Steps >= 5, $"only {organizer.Steps} world steps");
        // Loose bounds: engine stalls on a loaded machine shift time between neighbouring steps.
        Assert.InRange(organizer.TotalDt - organizer.FirstDt, covered * 0.5, covered * 2 + 0.25);
    }
}
