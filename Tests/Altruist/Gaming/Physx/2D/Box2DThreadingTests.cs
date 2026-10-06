/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.Gaming;
using Altruist.Physx;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;

namespace Tests.Gaming.Physx.TwoD;

/// <summary>
/// Independent Box2D worlds stepped on several threads at once must behave exactly as when they
/// are stepped one after the other (Box2DSharp's static contact pools are made per-thread).
/// </summary>
[Collection(Tests.Gaming.Engine.CpuHeavyCollection.Name)]
public class Box2DThreadingTests
{
    /// <summary>A pile of every shape pair (circle, box, polygon against each other, an edge floor and chain walls).</summary>
    private sealed class Pile
    {
        public readonly IPhysxWorldEngine2D World;
        public readonly List<IPhysxBody2D> Bodies = new();
        public int Begins;
        public int Ends;

        public Pile(int seed)
        {
            World = PhysxWorldEngine2D.Create(new PhysxWorldSettings2D { Gravity = new Vector2(0, -10), VelocityIterations = 8, PositionIterations = 3 });
            World.SetContactListener(new Counter(this));
            var floor = World.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Static });
            World.CreateFixture(floor, new PhysxFixtureDef2D { Shape = PhysxShape2D.Edge(new Vector2(-12, 0), new Vector2(12, 0)) });
            World.CreateFixture(floor, new PhysxFixtureDef2D
            {
                Shape = PhysxShape2D.Chain(new[] { new Vector2(-12, 0), new Vector2(-12, 30), new Vector2(-11, 30) }, loop: false),
            });
            World.CreateFixture(floor, new PhysxFixtureDef2D
            {
                Shape = PhysxShape2D.Chain(new[] { new Vector2(11, 30), new Vector2(12, 30), new Vector2(12, 0) }, loop: false),
            });
            var rnd = new Random(seed);
            for (var i = 0; i < 40; i++)
            {
                var body = World.CreateBody(new PhysxBodyDef2D
                {
                    Type = PhysxBodyType.Dynamic,
                    Position = new Vector2((float)(rnd.NextDouble() * 20 - 10), 1 + i * 0.7f),
                    Angle = (float)rnd.NextDouble(),
                });
                var shape = (i % 3) switch
                {
                    0 => PhysxShape2D.Circle(0.3f + (float)rnd.NextDouble() * 0.3f),
                    1 => PhysxShape2D.Box(0.3f + (float)rnd.NextDouble() * 0.3f, 0.25f),
                    _ => PhysxShape2D.Polygon(new[] { new Vector2(-0.4f, -0.3f), new Vector2(0.4f, -0.3f), new Vector2(0, 0.45f) }),
                };
                World.CreateFixture(body, new PhysxFixtureDef2D { Shape = shape, Density = 1, Friction = 0.4f, Restitution = 0.2f });
                body.LinearVelocity = new Vector2((float)(rnd.NextDouble() * 6 - 3), 0);
                Bodies.Add(body);
            }
        }

        public void Step() => World.Step(1f / 60f);

        public ulong Hash()
        {
            var h = 1469598103934665603UL;
            void Mix(float f) => h = (h ^ (uint)BitConverter.SingleToInt32Bits(f)) * 1099511628211UL;
            foreach (var b in Bodies)
            {
                Mix(b.Position.X);
                Mix(b.Position.Y);
                Mix(b.LinearVelocity.X);
                Mix(b.LinearVelocity.Y);
                Mix(b.RotationZ);
                Mix(b.AngularVelocityZ);
            }
            return (h ^ (ulong)Begins * 31) ^ ((ulong)Ends << 20);
        }

        private sealed class Counter(Pile pile) : IPhysxContactListener2D
        {
            public void BeginContact(IPhysxContact2D contact) => pile.Begins++;
            public void EndContact(IPhysxContact2D contact) => pile.Ends++;
        }
    }

    private const int Worlds = 16;
    private const int Steps = 400;

    private static ulong[] RunSerial()
    {
        var piles = Enumerable.Range(0, Worlds).Select(i => new Pile(i)).ToList();
        for (var s = 0; s < Steps; s++)
            foreach (var p in piles) p.Step();
        return piles.Select(p => p.Hash()).ToArray();
    }

    [Fact]
    public void The_per_thread_contact_pools_are_installed()
    {
        Assert.True(PhysxWorldEngine2D.ParallelWorldsSupported);
        Assert.True(Box2DThreading.EnsureInstalled());
        Assert.Null(Box2DThreading.FailureReason);
    }

    [Fact]
    public void Worlds_stepped_in_parallel_match_worlds_stepped_one_by_one_bit_for_bit()
    {
        var serial = RunSerial();
        Assert.Equal(Worlds, serial.Distinct().Count()); // the piles really differ
        var scheduler = new StepScheduler(8);
        for (var run = 0; run < 3; run++)
        {
            var piles = Enumerable.Range(0, Worlds).Select(i => new Pile(i)).ToList();
            for (var s = 0; s < Steps; s++) scheduler.ForEach(piles, p => p.Step());
            Assert.Equal(serial, piles.Select(p => p.Hash()).ToArray());
        }
    }

    [Fact]
    public void A_world_may_move_to_another_thread_between_steps()
    {
        var serial = RunSerial();
        var piles = Enumerable.Range(0, Worlds).Select(i => new Pile(i)).ToList();
        var threads = new HashSet<int>[Worlds];
        for (var i = 0; i < Worlds; i++) threads[i] = new HashSet<int>();
        using var first = new Worker();
        using var second = new Worker();
        var half = Worlds / 2;
        for (var s = 0; s < Steps; s++)
        {
            // Two threads at once, swapping halves every step: every world changes thread each step.
            var (x, y) = s % 2 == 0 ? (first, second) : (second, first);
            void StepRange(int from, int to)
            {
                for (var i = from; i < to; i++)
                {
                    piles[i].Step();
                    threads[i].Add(Environment.CurrentManagedThreadId);
                }
            }
            Task.WaitAll(x.Run(() => StepRange(0, half)), y.Run(() => StepRange(half, Worlds)));
        }
        Assert.Equal(serial, piles.Select(p => p.Hash()).ToArray());
        Assert.All(threads, t => Assert.Equal(2, t.Count));
    }

    /// <summary>A dedicated thread that runs what it is given.</summary>
    private sealed class Worker : IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(Action Work, TaskCompletionSource Done)> _queue = new();
        private readonly Thread _thread;

        public Worker()
        {
            _thread = new Thread(() =>
            {
                foreach (var (work, done) in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        work();
                        done.SetResult();
                    }
                    catch (Exception ex) { done.SetException(ex); }
                }
            }) { IsBackground = true };
            _thread.Start();
        }

        public Task Run(Action work)
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _queue.Add((work, done));
            return done.Task;
        }

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
            _queue.Dispose();
        }
    }

    [Fact]
    public async Task Many_worlds_on_many_threads_never_corrupt_each_other()
    {
        var reference = new Pile(99);
        for (var s = 0; s < 300; s++) reference.Step();
        var expected = reference.Hash();
        var tasks = Enumerable.Range(0, 24).Select(_ => Task.Run(() =>
        {
            var pile = new Pile(99);
            for (var s = 0; s < 300; s++) pile.Step();
            return pile.Hash();
        }));
        var hashes = await Task.WhenAll(tasks);
        Assert.All(hashes, h => Assert.Equal(expected, h));
    }

    [Fact]
    public void Destroying_bodies_returns_their_contacts_to_the_pool_of_any_thread()
    {
        var serial = RunWithDestroys(parallel: false);
        var parallel = RunWithDestroys(parallel: true);
        Assert.Equal(serial, parallel);
    }

    private static ulong[] RunWithDestroys(bool parallel)
    {
        var piles = Enumerable.Range(0, 8).Select(i => new Pile(100 + i)).ToList();
        var scheduler = new StepScheduler(parallel ? 4 : 1);
        for (var s = 0; s < 300; s++)
        {
            scheduler.ForEach(piles, p =>
            {
                p.Step();
                // Remove a resting body now and then: its contacts are destroyed mid-run.
                if (s % 50 == 49 && p.Bodies.Count > 0)
                {
                    p.World.RemoveBody(p.Bodies[^1]);
                    p.Bodies.RemoveAt(p.Bodies.Count - 1);
                }
            });
        }
        return piles.Select(p => p.Hash()).ToArray();
    }
}
