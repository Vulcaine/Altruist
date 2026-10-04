/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Diagnostics;

using Altruist;
using Altruist.Engine;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Gaming.TwoD;
using Altruist.Physx;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Moq;

namespace Tests.Gaming.Engine;

/// <summary>
/// Backward compatibility of 0.9.9: worker mode (the default) keeps the threading of 0.9.7 and
/// earlier, and the 2D/3D organizers keep their 25 Hz entity-sync rate unless configured.
/// </summary>
public sealed class EngineCompatibilityTests
{
    private static IServerStatus AliveStatus()
    {
        var status = new Mock<IServerStatus>();
        status.SetupGet(s => s.Status).Returns(ReadyState.Alive);
        return status.Object;
    }

    /// <summary>Thread of one frame: the next-tick delegate's view, and the cycle's.</summary>
    private static (List<(bool Pool, string? Name)> Commands, List<(bool Pool, string? Name)> Cycles) RunLive(string? worldStep)
    {
        var commands = new List<(bool, string?)>();
        var cycles = new List<(bool, string?)>();
        var engine = new AltruistEngine(AliveStatus(), new ServiceCollection().BuildServiceProvider(),
            new WorldCoordinator(Array.Empty<IWorldStepper>()), framerateHz: 120, worldStep: worldStep);
        engine.ScheduleTask(new Action(() =>
        {
            lock (cycles)
                cycles.Add((Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.Name));
        }));
        engine.Start(default);
        try
        {
            var sw = Stopwatch.StartNew();
            while (true)
            {
                lock (commands)
                {
                    if (commands.Count >= 30)
                        break;
                }
                engine.WaitForNextTick(() =>
                {
                    lock (commands)
                        commands.Add((Thread.CurrentThread.IsThreadPoolThread, Thread.CurrentThread.Name));
                });
                Assert.True(sw.Elapsed < TimeSpan.FromSeconds(20), "the engine did not run frames");
                Thread.Sleep(5);
            }
        }
        finally
        {
            engine.Stop();
        }
        lock (cycles)
            return (commands, cycles.ToList());
    }

    [Fact]
    public void Worker_mode_frames_continue_on_the_thread_pool_as_before_0_9_8()
    {
        var (commands, cycles) = RunLive(worldStep: null);

        // 0.9.7: the frame resumed after the timer wait on a pool thread (ConfigureAwait(false)).
        // Only a frame whose timer had already fired before the first wait may stay on EngineThread.
        Assert.True(commands.Count(c => c.Pool) >= commands.Count - 1,
            $"next-tick ran on the pool in {commands.Count(c => c.Pool)} of {commands.Count} frames");
        Assert.True(cycles.Count(c => c.Pool) >= cycles.Count - 1,
            $"cycles ran on the pool in {cycles.Count(c => c.Pool)} of {cycles.Count} frames");
    }

    [Fact]
    public void Inline_mode_frames_run_on_the_engine_thread()
    {
        var (commands, cycles) = RunLive(worldStep: "inline");

        Assert.All(commands, c => Assert.Equal("EngineThread", c.Name));
        Assert.All(cycles, c => Assert.Equal("EngineThread", c.Name));
    }

    [Fact]
    public void Worker_mode_awaits_async_next_tick_delegates_without_blocking_the_engine_thread()
    {
        var engine = new AltruistEngine(AliveStatus(), new ServiceCollection().BuildServiceProvider(),
            new WorldCoordinator(Array.Empty<IWorldStepper>()), framerateHz: 120);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var order = new ConcurrentQueue<string>();
        engine.Start(default);
        try
        {
            engine.WaitForNextTick(new Func<Task>(async () =>
            {
                order.Enqueue("async:start");
                await gate.Task;
                order.Enqueue("async:end");
            }));
            engine.WaitForNextTick(() => order.Enqueue("after"));
            var sw = Stopwatch.StartNew();
            while (!order.Contains("async:start") && sw.Elapsed < TimeSpan.FromSeconds(10))
                Thread.Sleep(5);
            Thread.Sleep(50);
            // Delegates run in order: the next one waits for the async one to finish.
            Assert.DoesNotContain("after", order);
            gate.SetResult();
            while (!order.Contains("after") && sw.Elapsed < TimeSpan.FromSeconds(10))
                Thread.Sleep(5);
        }
        finally
        {
            engine.Stop();
        }

        Assert.Equal(new[] { "async:start", "async:end", "after" }, order);
    }

    [Fact]
    public void Organizers_sync_at_25_hz_by_default_and_take_the_configured_rate()
    {
        var organizer2D = new GameWorldOrganizer2D(new Mock<IWorldPartitioner2D>().Object, new Mock<ICacheProvider>().Object,
            new Mock<IPhysxWorldEngineFactory2D>().Object, Array.Empty<IWorldIndex2D>());
        Assert.Equal(25f, organizer2D.EntitySyncHz);
        var organizer3D = new GameWorldOrganizer3D(new Mock<IWorldLoader3D>().Object, Array.Empty<IWorldIndex3D>());
        Assert.Equal(25f, organizer3D.EntitySyncHz);

        var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["altruist:game:worlds:entity-sync-hz"] = "60",
        }).Build();
        var services = new ServiceCollection();
        services.AddSingleton(new Mock<IWorldPartitioner2D>().Object);
        services.AddSingleton(new Mock<ICacheProvider>().Object);
        services.AddSingleton(new Mock<IPhysxWorldEngineFactory2D>().Object);
        services.AddSingleton<IEnumerable<IWorldIndex2D>>(Array.Empty<IWorldIndex2D>());
        services.AddSingleton(new Mock<IWorldLoader3D>().Object);
        services.AddSingleton<IEnumerable<IWorldIndex3D>>(Array.Empty<IWorldIndex3D>());
        using var sp = services.BuildServiceProvider();

        var configured2D = (GameWorldOrganizer2D)DependencyResolver.CreateWithConfiguration(sp, cfg, typeof(GameWorldOrganizer2D), NullLogger.Instance);
        var configured3D = (GameWorldOrganizer3D)DependencyResolver.CreateWithConfiguration(sp, cfg, typeof(GameWorldOrganizer3D), NullLogger.Instance);
        Assert.Equal(60f, configured2D.EntitySyncHz);
        Assert.Equal(60f, configured3D.EntitySyncHz);

        var unset = (GameWorldOrganizer2D)DependencyResolver.CreateWithConfiguration(sp, new ConfigurationBuilder().Build(),
            typeof(GameWorldOrganizer2D), NullLogger.Instance);
        Assert.Equal(25f, unset.EntitySyncHz);
    }
}
