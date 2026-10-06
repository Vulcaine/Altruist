/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;

using Altruist;
using Altruist.Engine;
using Altruist.Gaming;
using Altruist.Gaming.TwoD;
using Altruist.Physx.TwoD;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Tests.Gaming.Engine;

// ------------------------------------------------------------------ fixtures

/// <summary>Records every call the coordinator makes, as "name:phase[:detail]" strings.</summary>
public sealed class RecordingStepper : IWorldStepper
{
    private readonly string _name;
    private readonly List<string> _log;
    public readonly List<FixedStep> Steps = new();
    public readonly List<FrameInfo> Before = new();
    public readonly List<FrameInfo> After = new();
    public readonly List<float> VariableDts = new();
    public Action? OnFixedStep;

    public RecordingStepper(string name, List<string> log, StepMode mode = StepMode.Variable, int hz = 0)
    {
        _name = name;
        _log = log;
        Mode = mode;
        FixedHz = hz;
    }

    public StepMode Mode { get; }
    public int FixedHz { get; }

    public void BeforeSteps(in FrameInfo frame)
    {
        Before.Add(frame);
        _log.Add($"{_name}:before:{frame.FixedSteps}");
    }

    public void Step(float dt)
    {
        VariableDts.Add(dt);
        _log.Add($"{_name}:step");
    }

    public void FixedStep(in FixedStep step)
    {
        Steps.Add(step);
        _log.Add($"{_name}:fixed:{step.Step}");
        OnFixedStep?.Invoke();
    }

    public void AfterSteps(in FrameInfo frame)
    {
        After.Add(frame);
        _log.Add($"{_name}:after");
    }
}

public sealed class ThrowingStepper : IWorldStepper
{
    public StepMode Mode => StepMode.Fixed;
    public int FixedHz => 60;
    public void FixedStep(in FixedStep step) => throw new InvalidOperationException("stepper boom");
}

public sealed class EveryFrameCycleService
{
    public int Runs;

    [Cycle]
    public void Tick() => Runs++;
}

public sealed class ConfiguredCycleService
{
    public int Runs;
    public int DefaultRuns;
    public int FallbackRuns;

    [Cycle(Config = "test:cycle:hz", Default = 10, Unit = CycleUnit.Hz)]
    public void Configured() => Runs++;

    [Cycle(Config = "test:cycle:missing", Default = 20, Unit = CycleUnit.Hz)]
    public void UsesDefault() => DefaultRuns++;

    [Cycle(Config = "test:cycle:missing-too")]
    public void EveryFrameWithoutDefault() => FallbackRuns++;
}

// ------------------------------------------------------------------ FixedStepClock

/// <summary>Fixed-step accumulator (0.9.8): step counts, clamp, max steps and the overrun policies.</summary>
public sealed class FixedStepClockTests
{
    [Fact]
    public void Frames_at_twice_the_step_rate_alternate_zero_and_one_step_and_give_exactly_the_rate()
    {
        var clock = new FixedStepClock(60);
        var frame = clock.Dt / 2; // what a 120 Hz engine hands over (float frame times)
        var pattern = new List<int>();
        var total = 0;
        for (var i = 0; i < 120; i++)
        {
            var n = clock.Advance(frame, out _, out var overrun);
            Assert.False(overrun);
            pattern.Add(n);
            total += n;
        }

        Assert.Equal(60, total);
        Assert.Equal(Enumerable.Range(0, 120).Select(i => i % 2), pattern);
        Assert.Equal(60, clock.TotalSteps);
    }

    [Fact]
    public void Wall_clock_frames_average_to_the_step_rate()
    {
        var clock = new FixedStepClock(60);
        var total = 0;
        for (var i = 0; i < 1200; i++)
            total += clock.Advance(1.0 / 120, out _, out _);
        Assert.InRange(total, 599, 600);
    }

    [Fact]
    public void A_long_frame_is_clamped_and_capped_and_drop_discards_the_rest()
    {
        var clock = new FixedStepClock(60, maxFrameDelta: 0.25, maxStepsPerFrame: 8, overrun: OverrunPolicy.Drop);
        var n = clock.Advance(1.0, out var clamped, out var overrun);
        Assert.Equal(8, n);
        Assert.Equal(0.25, clamped);
        Assert.True(overrun);
        Assert.Equal(0, clock.Accumulator);
        Assert.Equal(0, clock.Advance(0, out _, out _));
    }

    [Fact]
    public void Carry_steps_the_backlog_in_the_next_frames()
    {
        var clock = new FixedStepClock(60, overrun: OverrunPolicy.Carry);
        Assert.Equal(8, clock.Advance(0.25, out _, out var overrun));
        Assert.True(overrun);
        var later = 0;
        for (var i = 0; i < 5; i++)
            later += clock.Advance(0, out _, out _);
        // 0.25 s = 14.99999 float steps of 1/60: 14 in total, the remainder stays below one step.
        Assert.Equal(14, 8 + later);
        Assert.True(clock.Accumulator < clock.Dt);
    }

    [Fact]
    public void Carry_backlog_is_bounded_by_max_frame_delta()
    {
        var clock = new FixedStepClock(60, maxFrameDelta: 0.25, maxStepsPerFrame: 2, overrun: OverrunPolicy.Carry);
        for (var i = 0; i < 100; i++)
            clock.Advance(0.25, out _, out _);
        Assert.True(clock.Accumulator <= 0.25);
    }

    [Fact]
    public void Slow_motion_never_builds_a_backlog_but_keeps_the_sub_step_phase()
    {
        var clock = new FixedStepClock(60, overrun: OverrunPolicy.SlowMotion);
        Assert.Equal(8, clock.Advance(0.25, out _, out var overrun));
        Assert.True(overrun);
        Assert.True(clock.Accumulator < clock.Dt);
        Assert.Equal(0, clock.Advance(0, out _, out _));

        // Half a step pending, then a long frame: drop loses the phase, slow motion keeps it.
        var drop = new FixedStepClock(60, overrun: OverrunPolicy.Drop);
        var slow = new FixedStepClock(60, overrun: OverrunPolicy.SlowMotion);
        foreach (var c in new[] { drop, slow })
        {
            Assert.Equal(0, c.Advance(clock.Dt * 0.5, out _, out _));
            Assert.Equal(8, c.Advance(clock.Dt * 9, out _, out _));
        }
        Assert.Equal(0, drop.Accumulator);
        Assert.InRange(slow.Accumulator, clock.Dt * 0.4, clock.Dt * 0.6);
    }

    [Fact]
    public void Negative_frame_time_counts_as_zero_and_the_same_frames_give_the_same_steps()
    {
        var a = new FixedStepClock(60);
        Assert.Equal(0, a.Advance(-1, out var clamped, out _));
        Assert.Equal(0, clamped);

        var frames = new[] { 0.004, 0.02, 0.0083, 0.3, 0.001, 0.05, 0.0167, 0.0166 };
        var b = new FixedStepClock(60);
        var c = new FixedStepClock(60);
        Assert.Equal(frames.Select(f => b.Advance(f, out _, out _)), frames.Select(f => c.Advance(f, out _, out _)));
        Assert.Equal(b.Accumulator, c.Accumulator);
    }

    [Theory]
    [InlineData(null, OverrunPolicy.Drop)]
    [InlineData("drop", OverrunPolicy.Drop)]
    [InlineData("carry", OverrunPolicy.Carry)]
    [InlineData("slow-motion", OverrunPolicy.SlowMotion)]
    [InlineData("SlowMotion", OverrunPolicy.SlowMotion)]
    public void Overrun_config_values_parse(string? value, OverrunPolicy expected) =>
        Assert.Equal(expected, FixedStepClock.ParseOverrun(value));

    [Fact]
    public void Invalid_settings_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedStepClock(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedStepClock(60, maxFrameDelta: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new FixedStepClock(60, maxStepsPerFrame: 0));
        Assert.Throws<ArgumentException>(() => FixedStepClock.ParseOverrun("rewind"));
    }
}

// ------------------------------------------------------------------ WorldCoordinator

/// <summary>The composite world step (0.9.8): variable and fixed steppers, order, isolation.</summary>
public sealed class WorldCoordinatorTests
{
    [Fact]
    public void Variable_steppers_get_the_real_frame_time_once_per_frame()
    {
        var log = new List<string>();
        var v = new RecordingStepper("v", log);
        var coordinator = new WorldCoordinator(new IWorldStepper[] { v });

        coordinator.Step(0.01f);
        coordinator.Step(0.5f); // not clamped for variable steppers (backward compatible)

        Assert.Equal(new[] { 0.01f, 0.5f }, v.VariableDts);
        Assert.Equal(new[] { "v:before:0", "v:step", "v:after", "v:before:0", "v:step", "v:after" }, log);
        Assert.Equal(2, v.After[1].Frame);
    }

    [Fact]
    public void Fixed_steppers_get_a_constant_dt_and_consecutive_step_numbers()
    {
        var log = new List<string>();
        var f = new RecordingStepper("f", log, StepMode.Fixed, 60);
        var coordinator = new WorldCoordinator(new IWorldStepper[] { f });
        var frame = (1f / 60) / 2;

        for (var i = 0; i < 120; i++)
            coordinator.Step(frame);

        Assert.Equal(60, f.Steps.Count);
        Assert.Equal(Enumerable.Range(1, 60).Select(i => (long)i), f.Steps.Select(s => s.Step));
        Assert.All(f.Steps, s => Assert.Equal(1f / 60, s.Dt));
        Assert.All(f.Steps, s => Assert.Equal((0, 1), (s.IndexInFrame, s.StepsInFrame)));
        Assert.Equal(120, f.Before.Count);
        Assert.Equal(120, f.After.Count);
        Assert.Equal(60, f.Before.Sum(b => b.FixedSteps));
        // Before → steps → after, every frame.
        Assert.Equal(new[] { "f:before:0", "f:after", "f:before:1", "f:fixed:1", "f:after" }, log.Take(5));
    }

    [Fact]
    public void A_long_frame_runs_several_steps_in_one_frame_with_indices()
    {
        var f = new RecordingStepper("f", new List<string>(), StepMode.Fixed, 60);
        var coordinator = new WorldCoordinator(new IWorldStepper[] { f }, maxFrameDelta: 0.25, maxStepsPerFrame: 8);

        coordinator.Step(0.051f);

        Assert.Equal(3, f.Before.Single().FixedSteps);
        Assert.Equal(new[] { 0, 1, 2 }, f.Steps.Select(s => s.IndexInFrame));
        Assert.All(f.Steps, s => Assert.Equal(3, s.StepsInFrame));
        Assert.False(f.After.Single().Overrun);

        coordinator.Step(1f);
        Assert.Equal(8, f.Before[1].FixedSteps);
        Assert.True(f.After[1].Overrun);
        Assert.Equal(0.25, f.After[1].Dt);
        Assert.Equal(1.0, f.After[1].RealDt, 6);
    }

    [Fact]
    public void Steppers_with_different_rates_keep_their_own_clock_and_run_in_registration_order()
    {
        var log = new List<string>();
        var a = new RecordingStepper("a", log, StepMode.Fixed, 60);
        var v = new RecordingStepper("v", log);
        var b = new RecordingStepper("b", log, StepMode.Fixed, 25);
        var coordinator = new WorldCoordinator(new IWorldStepper[] { a, v, b });

        for (var i = 0; i < 120; i++)
            coordinator.Step(1f / 120);

        Assert.InRange(a.Steps.Count, 59, 60);
        Assert.InRange(b.Steps.Count, 24, 25);
        Assert.Equal(120, v.VariableDts.Count);
        var firstFrame = log.TakeWhile(l => l != "b:after").Append("b:after").ToList();
        Assert.Equal(new[] { "a:before:0", "a:after", "v:before:0", "v:step", "v:after", "b:before:0", "b:after" }, firstFrame);
        Assert.Same(a, coordinator.Steppers[0]);
        Assert.Equal(25, coordinator.ClockOf(b)!.Hz);
        Assert.Null(coordinator.ClockOf(v));
    }

    [Fact]
    public void A_throwing_stepper_does_not_stop_the_others()
    {
        var f = new RecordingStepper("f", new List<string>(), StepMode.Fixed, 60);
        var coordinator = new WorldCoordinator(new IWorldStepper[] { new ThrowingStepper(), f });
        for (var i = 0; i < 10; i++)
            coordinator.Step(1f / 60);
        Assert.Equal(10, f.Steps.Count);
    }

    [Fact]
    public void One_instance_listed_twice_is_stepped_once_and_a_fixed_stepper_needs_a_rate()
    {
        var f = new RecordingStepper("f", new List<string>(), StepMode.Fixed, 60);
        var coordinator = new WorldCoordinator(new IWorldStepper[] { f, f });
        coordinator.Step(1f / 60);
        Assert.Single(f.Steps);

        var broken = new WorldCoordinator(new IWorldStepper[] { new RecordingStepper("x", new List<string>(), StepMode.Fixed, 0) });
        Assert.Throws<InvalidOperationException>(() => broken.Step(0.01f));
    }

    [Fact]
    public void Steppers_are_resolved_lazily_and_config_keys_apply_through_DI()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["altruist:game:engine:fixed-step:max-frame-delta"] = "0.1",
            ["altruist:game:engine:fixed-step:max-steps-per-frame"] = "3",
            ["altruist:game:engine:fixed-step:overrun"] = "slow-motion",
        }).Build();
        var services = new ServiceCollection();
        var f = new RecordingStepper("f", new List<string>(), StepMode.Fixed, 60);
        services.AddSingleton<IWorldStepper>(f);
        services.AddSingleton<IGameWorldOrganizer>(sp =>
            (WorldCoordinator)DependencyResolver.CreateWithConfiguration(sp, cfg, typeof(WorldCoordinator), NullLogger.Instance));
        using var sp = services.BuildServiceProvider();

        var coordinator = Assert.IsType<WorldCoordinator>(sp.GetRequiredService<IGameWorldOrganizer>());
        Assert.Equal(0.1, coordinator.MaxFrameDelta);
        Assert.Equal(3, coordinator.MaxStepsPerFrame);
        Assert.Equal(OverrunPolicy.SlowMotion, coordinator.Overrun);

        coordinator.Step(1f);
        Assert.Equal(3, f.Steps.Count);
    }

    [Fact]
    public void Altruist_registration_makes_the_coordinator_the_organizer_and_steps_service_steppers_once()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["altruist:game:engine:framerateHz"] = "120",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddSingleton<IConfiguration>(cfg);
        DependencyResolver.EnsureConverters(services, cfg, NullLogger.Instance);
        var reg = new List<string>();
        foreach (var t in new[] { typeof(ForceRuntime2D), typeof(WorldCoordinator) })
            AltruistDIServiceConfig.RegisterServiceType(services, cfg, NullLogger.Instance, reg, t);
        using var sp = services.BuildServiceProvider();

        var coordinator = Assert.IsType<WorldCoordinator>(sp.GetRequiredService<IGameWorldOrganizer>());
        var runtime = sp.GetRequiredService<IForceRuntime2D>();
        Assert.Same(runtime, Assert.Single(coordinator.Steppers));
        Assert.Equal(25, coordinator.ClockOf(coordinator.Steppers[0])!.Hz);
    }
}

// ------------------------------------------------------------------ engine: inline mode, cycles, timers

/// <summary>
/// The engine frame pipeline (0.9.8): world-step inline, plain and configured [Cycle] rates,
/// ScheduleOnce / ScheduleAtFrame, RunOffTick, the engine clock and the manual test driver.
/// </summary>
// Wall-clock timings: run alone, not next to the CPU-heavy tests.
[Collection(Tests.Gaming.Engine.CpuHeavyCollection.Name)]
public sealed class EngineFramePipelineTests
{
    private static IServerStatus AliveStatus()
    {
        var status = new Mock<IServerStatus>();
        status.SetupGet(s => s.Status).Returns(ReadyState.Alive);
        return status.Object;
    }

    private static (AltruistEngine Engine, EngineTestDriver Driver) Manual(IGameWorldOrganizer? organizer = null,
        string worldStep = "inline", IServiceProvider? services = null, int hz = 120)
    {
        var clock = new ManualEngineClock();
        var engine = new AltruistEngine(AliveStatus(), services ?? new ServiceCollection().BuildServiceProvider(),
            organizer ?? new Mock<IGameWorldOrganizer>().Object, framerateHz: hz, worldStep: worldStep, clock: clock);
        return (engine, new EngineTestDriver(engine, clock));
    }

    [Fact]
    public void Inline_mode_runs_next_tick_cycles_effects_then_the_world_step_in_that_order()
    {
        var log = new List<string>();
        var stepper = new RecordingStepper("room", log, StepMode.Fixed, 60);
        var (engine, driver) = Manual(new WorldCoordinator(new IWorldStepper[] { stepper }));
        engine.ScheduleTask(new Action(() => log.Add("cycle")));
        engine.ScheduleOnce(TimeSpan.Zero, () => log.Add("timer"));
        driver.Post(() => log.Add("command"));

        driver.AdvanceFrames(1, 1.0 / 60);

        Assert.Equal(new[] { "command", "cycle", "timer", "room:before:1", "room:fixed:1", "room:after" }, log);
        Assert.Equal(WorldStepMode.Inline, engine.WorldStep);
        Assert.Equal(1, engine.Frame);
    }

    [Fact]
    public void Worker_mode_is_the_default_and_leaves_the_world_step_to_the_worker()
    {
        var steps = 0;
        var organizer = new Mock<IGameWorldOrganizer>();
        organizer.Setup(o => o.Step(It.IsAny<float>())).Callback(() => steps++);
        var clock = new ManualEngineClock();
        var engine = new AltruistEngine(AliveStatus(), new ServiceCollection().BuildServiceProvider(), organizer.Object,
            framerateHz: 120, clock: clock);
        new EngineTestDriver(engine, clock).AdvanceFrames(5, 1.0 / 120);

        Assert.Equal(WorldStepMode.Worker, engine.WorldStep);
        Assert.Equal(0, steps);
        Assert.Throws<ArgumentException>(() => AltruistEngine.ParseWorldStep("sideways"));
    }

    [Fact]
    public void Inline_mode_on_a_live_engine_runs_every_phase_on_the_engine_thread_without_overlap()
    {
        var threads = new ConcurrentDictionary<string, string?>();
        var inPhase = 0;
        var overlaps = 0;
        var stepperRuns = 0;
        void Phase(string name)
        {
            if (Interlocked.Exchange(ref inPhase, 1) == 1)
                Interlocked.Increment(ref overlaps);
            threads[name + ":" + Environment.CurrentManagedThreadId] = Thread.CurrentThread.Name;
            Thread.SpinWait(500);
            Interlocked.Exchange(ref inPhase, 0);
        }

        var stepper = new RecordingStepper("room", new List<string>(), StepMode.Fixed, 60);
        stepper.OnFixedStep = () =>
        {
            Phase("step");
            Interlocked.Increment(ref stepperRuns);
        };
        var engine = new AltruistEngine(AliveStatus(), new ServiceCollection().BuildServiceProvider(),
            new WorldCoordinator(new IWorldStepper[] { stepper }), framerateHz: 120, worldStep: "inline");
        engine.ScheduleTask(new Action(() => Phase("cycle")));
        engine.Start(default);
        try
        {
            var sw = Stopwatch.StartNew();
            while (Volatile.Read(ref stepperRuns) < 30)
            {
                engine.WaitForNextTick(() => Phase("next-tick"));
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), "the inline world step did not run");
                Thread.Sleep(5);
            }
        }
        finally
        {
            engine.Stop();
        }

        Assert.Equal(0, Volatile.Read(ref overlaps));
        Assert.All(threads.Values, name => Assert.Equal("EngineThread", name));
        Assert.Single(threads.Keys.Select(k => k.Split(':')[1]).Distinct());
    }

    [Fact]
    public void A_plain_cycle_runs_every_frame()
    {
        var services = new ServiceCollection();
        services.AddSingleton<EveryFrameCycleService>();
        using var sp = services.BuildServiceProvider();
        var (engine, driver) = Manual(services: sp, hz: 120);
        var service = sp.GetRequiredService<EveryFrameCycleService>();
        new MethodScheduler(engine, sp).RegisterMethods(sp);

        driver.AdvanceFrames(10, 1.0 / 120);

        // Before 0.9.8 a plain [Cycle] ran once every framerateHz frames (once per second).
        Assert.Equal(10, service.Runs);
        Assert.Equal("⚡ every frame", new CycleAttribute().ToString());
    }

    [Fact]
    public void A_null_rate_task_runs_every_frame()
    {
        var (engine, driver) = Manual();
        var runs = 0;
        engine.ScheduleTask(new Action(() => runs++));
        driver.AdvanceFrames(7, 1.0 / 120);
        Assert.Equal(7, runs);
    }

    [Fact]
    public void A_configured_cycle_reads_its_rate_from_config_and_falls_back_to_the_default()
    {
        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["test:cycle:hz"] = "30",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(cfg);
        services.AddSingleton<ConfiguredCycleService>();
        using var sp = services.BuildServiceProvider();
        var (engine, driver) = Manual(services: sp, hz: 120);
        var service = sp.GetRequiredService<ConfiguredCycleService>();
        new MethodScheduler(engine, sp).RegisterMethods(sp);

        driver.AdvanceFrames(120, 1.0 / 120);

        // 30 Hz on a 120 Hz engine: every 4th frame on average (the cadence does not drift to every 5th).
        Assert.InRange(service.Runs, 29, 31);
        Assert.InRange(service.DefaultRuns, 19, 21);
        Assert.Equal(120, service.FallbackRuns);
        Assert.StartsWith("⚙ test:cycle:hz", typeof(ConfiguredCycleService).GetMethod(nameof(ConfiguredCycleService.Configured))!
            .GetCustomAttributes(typeof(CycleAttribute), false).Cast<CycleAttribute>().Single().ToString());
    }

    [Fact]
    public void ScheduleOnce_fires_once_after_the_delay_on_the_engine_clock_and_can_be_cancelled()
    {
        var (engine, driver) = Manual();
        var fired = new List<long>();
        engine.ScheduleOnce(TimeSpan.FromSeconds(0.1), () => fired.Add(engine.Frame));
        var cancelled = engine.ScheduleOnce(TimeSpan.FromSeconds(0.05), () => fired.Add(-1));
        Assert.True(engine.CancelEffect(cancelled));

        driver.AdvanceFrames(60, 0.01);

        // 0.1 s of 10 ms frames: frame 10.
        Assert.Equal(new long[] { 10 }, fired);
    }

    [Fact]
    public void ScheduleAtFrame_fires_in_that_frame_and_timers_run_in_scheduling_order()
    {
        var (engine, driver) = Manual();
        var log = new List<string>();
        engine.ScheduleAtFrame(3, () => log.Add("b@" + engine.Frame));
        engine.ScheduleAtFrame(3, () => log.Add("c@" + engine.Frame));
        engine.ScheduleAtFrame(1, () => log.Add("a@" + engine.Frame));
        engine.ScheduleAtFrame(-5, () => log.Add("past@" + engine.Frame));

        driver.AdvanceFrames(5, 1.0 / 120);

        Assert.Equal(new[] { "a@1", "past@1", "b@3", "c@3" }, log);
    }

    [Fact]
    public void Effects_expire_on_the_engine_clock()
    {
        var (engine, driver) = Manual();
        var steps = 0;
        engine.ScheduleEffect(new CycleRate(1, CycleUnit.Ticks), DateTime.UtcNow.AddSeconds(0.5), _ => steps++);

        driver.AdvanceFrames(120, 1.0 / 120);

        // Manual time, not wall time: about 0.5 s of 120 Hz frames.
        Assert.InRange(steps, 55, 61);
    }

    [Fact]
    public async Task RunOffTick_hands_the_result_or_the_error_back_on_the_engine_loop()
    {
        var (engine, driver) = Manual();
        IAltruistEngine api = new EngineWithoutDiagnostics(engine);
        var gate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        (int Value, Exception? Error, long Frame)? result = null;
        Exception? failure = null;
        var failed = false;

        api.RunOffTick<int>(async _ =>
        {
            await gate.Task;
            return 42;
        }, (value, error) => result = (value, error, engine.Frame));
        api.RunOffTick(_ => throw new InvalidOperationException("db down"), error =>
        {
            failure = error;
            failed = true;
        });

        driver.AdvanceFrames(2, 1.0 / 120);
        Assert.Null(result);
        gate.SetResult(true);

        var sw = Stopwatch.StartNew();
        while (result is null || !failed)
        {
            Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), "RunOffTick never came back");
            await Task.Delay(5);
            driver.AdvanceFrames(1, 1.0 / 120);
        }

        Assert.Equal(42, result!.Value.Value);
        Assert.Null(result.Value.Error);
        Assert.True(result.Value.Frame > 2);
        Assert.IsType<InvalidOperationException>(failure);
    }

    [Fact]
    public void The_driver_refuses_a_running_engine()
    {
        var clock = new ManualEngineClock();
        var engine = new AltruistEngine(AliveStatus(), new ServiceCollection().BuildServiceProvider(),
            new Mock<IGameWorldOrganizer>().Object, framerateHz: 120, clock: clock);
        engine.Enable();
        Assert.Throws<InvalidOperationException>(() => new EngineTestDriver(engine, clock));
    }
}

// ------------------------------------------------------------------ force runtime

/// <summary>ForceRuntime2D/3D assumed a 25 Hz [Cycle]; a plain [Cycle] ran once per second, so forces moved 25× too slowly.</summary>
public sealed class ForceRuntimeFixedStepTests
{
    [Fact]
    public void Force_runtime_steps_at_25_hz_through_the_coordinator_whatever_the_frame_rate()
    {
        var body = new Mock<IPhysxBody2D>();
        var position = Vector2.Zero;
        body.SetupGet(b => b.Position).Returns(() => position);
        body.SetupSet(b => b.Position = It.IsAny<Vector2>()).Callback<Vector2>(p => position = p);

        var runtime = new ForceRuntime2D(NullLoggerFactory.Instance);
        Assert.Equal(StepMode.Fixed, ((IWorldStepper)runtime).Mode);
        Assert.Equal(25, ((IWorldStepper)runtime).FixedHz);
        runtime.ApplySustained(body.Object, new Vector2(1, 0), 10f);
        var coordinator = new WorldCoordinator(new IWorldStepper[] { runtime });

        for (var i = 0; i < 120; i++)
            coordinator.Step(1f / 120);

        // 1 s of frames: about 25 steps of 0.04 s at 1 m/s.
        Assert.InRange(position.X, 0.95f, 1.0001f);
    }
}
