using Altruist.Gaming;
using Altruist.Gaming.Flow;
using FluentAssertions;
using static Tests.Altruist.Numerics.BitExact;

namespace Tests.Altruist.Gaming.Flow;

/// <summary>TickPipeline, OverlapLatch, Countdown / TimerSet, ButtonEdges / InputLatch,
/// EntityRegistry, EventSink and the state machine as a match-phase host — each against the inline
/// simulation code it replaces (same order of effects, same float bits).</summary>
public class FlowStateTests
{
    // ── TickPipeline ───────────────────────────────────────────────────────

    private sealed class World
    {
        public readonly List<Unit> Units = new();
        public readonly List<string> Log = new();
        public bool Frozen;
    }

    private sealed class Unit
    {
        public int Id;
        public float Demolished;
    }

    /// <summary>A step shaped like a match simulation's: per-unit timers and input, a frozen gate, then physics.</summary>
    private static void InlineStep(World w, float dt)
    {
        w.Log.Add("clock");
        foreach (var u in w.Units)
        {
            if (u.Demolished > 0)
            {
                u.Demolished = MathF.Max(0, u.Demolished - dt);
                if (u.Demolished == 0) w.Log.Add($"respawn{u.Id}");
                continue;
            }
            w.Log.Add($"input{u.Id}");
            if (w.Frozen) continue;
            w.Log.Add($"move{u.Id}");
        }
        if (w.Frozen)
        {
            w.Log.Add("frozen-phase");
            return;
        }
        w.Log.Add("physics");
        foreach (var u in w.Units) w.Log.Add($"after{u.Id}");
    }

    private static TickPipeline<World>.Builder PipelineFor(float dt) => TickPipeline<World>.Create()
        .Step("clock", w => w.Log.Add("clock"))
        .ForEach("units", w => w.Units, s => s
            .SkipWhen("demolished", (w, u) =>
            {
                if (!Countdown.IsRunning(u.Demolished)) return false;
                if (Countdown.Tick(ref u.Demolished, dt)) w.Log.Add($"respawn{u.Id}");
                return true;
            })
            .Step("input", (w, u) => w.Log.Add($"input{u.Id}"))
            .SkipWhen("frozen", (w, _) => w.Frozen)
            .Step("move", (w, u) => w.Log.Add($"move{u.Id}")))
        .StopWhen("frozen", w =>
        {
            if (!w.Frozen) return false;
            w.Log.Add("frozen-phase");
            return true;
        })
        .Step("physics", w => w.Log.Add("physics"))
        .ForEach("after", w => w.Units, s => s.Step("after", (w, u) => w.Log.Add($"after{u.Id}")));

    private static World MakeWorld()
    {
        var w = new World();
        for (var i = 0; i < 4; i++) w.Units.Add(new Unit { Id = i, Demolished = i == 2 ? 0.05f : 0 });
        return w;
    }

    [Fact]
    public void TickPipeline_runs_steps_entities_skips_and_stops_in_the_inline_order()
    {
        const float dt = 1f / 60f;
        var pipeline = PipelineFor(dt).Build();
        var a = MakeWorld();
        var b = MakeWorld();
        for (var t = 0; t < 12; t++)
        {
            a.Frozen = b.Frozen = t % 5 == 0;
            InlineStep(a, dt);
            var completed = pipeline.Run(b, out var stoppedAt);
            completed.Should().Be(!b.Frozen);
            stoppedAt.Should().Be(b.Frozen ? pipeline.IndexOf("frozen") : -1);
        }
        b.Log.Should().Equal(a.Log);
        b.Log.Should().Contain("respawn2");
        for (var i = 0; i < 4; i++) Same(b.Units[i].Demolished, a.Units[i].Demolished);
    }

    [Fact]
    public void TickPipeline_steps_can_be_inserted_replaced_and_removed_by_name()
    {
        var log = new List<string>();
        var entity = new EntitySteps<int, int>().Step("a", (_, e) => log.Add($"a{e}")).Step("c", (_, e) => log.Add($"c{e}"));
        entity.After("a").Step("b", (_, e) => log.Add($"b{e}"));
        var b = TickPipeline<int>.Create()
            .Step("one", _ => log.Add("one"))
            .ForEach("each", _ => new[] { 1, 2 }, entity)
            .Step("three", _ => log.Add("three"));
        b.Before("three").Step("two", _ => log.Add("two"));
        b.Replacing("one").Step("first", _ => log.Add("first"));
        b.Remove("three");
        // Sub-steps edited after ForEach, before Build, still count.
        entity.Remove("c");
        var p = b.Build();
        p.Run(0);
        log.Should().Equal("first", "a1", "b1", "a2", "b2", "two");
        Enumerable.Range(0, p.Count).Select(p.NameOf).Should().Equal("first", "each", "two");
    }

    [Fact]
    public void TickPipeline_does_not_allocate_per_tick()
    {
        var w = new World();
        for (var i = 0; i < 4; i++) w.Units.Add(new Unit { Id = i });
        var counter = 0;
        var p = TickPipeline<World>.Create()
            .Step("a", _ => counter++)
            .ForEach("units", x => x.Units, s => s.SkipWhen("odd", (_, u) => (u.Id & 1) != 0).Step("count", (_, _) => counter++))
            .StopWhen("never", _ => false)
            .Build();
        for (var i = 0; i < 10; i++) p.Run(w);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 1000; i++) p.Run(w);
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        counter.Should().Be(1010 * 3);
    }

    // ── OverlapLatch / IdMask32 ────────────────────────────────────────────

    private sealed class Car
    {
        public int Id;
        public int Team;
        public bool Demolished;
        public int RamMask;
        public OverlapLatch32 Ram;
    }

    /// <summary>The ram finder: every overlap goes in the mask (even after a hit), only fresh ones may hit.</summary>
    private static Car? InlineFindRammed(Car a, List<Car> cars, Func<Car, Car, bool> overlaps, Func<Car, Car, bool> qualifies)
    {
        var prevMask = a.RamMask;
        var mask = 0;
        Car? hit = null;
        foreach (var b in cars)
        {
            if (b == a || b.Team == a.Team || b.Demolished) continue;
            if (!overlaps(a, b)) continue;
            var bit = 1 << (b.Id & 31);
            mask |= bit;
            if (hit is not null || (prevMask & bit) != 0) continue;
            if (qualifies(a, b)) hit = b;
        }
        a.RamMask = mask;
        return hit;
    }

    private static Car? LatchFindRammed(Car a, List<Car> cars, Func<Car, Car, bool> overlaps, Func<Car, Car, bool> qualifies)
    {
        a.Ram.Begin();
        Car? hit = null;
        foreach (var b in cars)
        {
            if (b == a || b.Team == a.Team || b.Demolished) continue;
            if (!overlaps(a, b)) continue;
            if (!a.Ram.Record(b.Id) || hit is not null) continue;
            if (qualifies(a, b)) hit = b;
        }
        return hit;
    }

    [Fact]
    public void OverlapLatch32_reproduces_the_ram_mask_semantics()
    {
        var gen = new Random(5);
        List<Car> Cars() => Enumerable.Range(0, 6).Select(i => new Car { Id = i * 7, Team = i % 2 }).ToList();
        var inline = Cars();
        var latch = Cars();
        var hits = 0;
        for (var step = 0; step < 3000; step++)
        {
            var seed = gen.Next();
            bool Overlaps(Car a, Car b) => ((seed >> ((a.Id + b.Id) % 23)) & 3) != 0;
            bool Qualifies(Car a, Car b) => ((seed >> ((a.Id * 3 + b.Id) % 19)) & 1) != 0;
            for (var i = 0; i < 6; i++) inline[i].Demolished = latch[i].Demolished = ((seed >> i) & 15) == 0;
            for (var i = 0; i < 6; i++)
            {
                // A demolished attacker skips the search (its mask stays as it was).
                if (inline[i].Demolished) continue;
                var h1 = InlineFindRammed(inline[i], inline, Overlaps, Qualifies);
                var h2 = LatchFindRammed(latch[i], latch, Overlaps, Qualifies);
                (h2?.Id ?? -1).Should().Be(h1?.Id ?? -1);
                if (h1 is not null) hits++;
                latch[i].Ram.Current.Should().Be(inline[i].RamMask);
            }
        }
        hits.Should().BeGreaterThan(100);
    }

    [Fact]
    public void IdMask32_collects_a_captured_set_like_the_inline_loop()
    {
        var cars = Enumerable.Range(0, 40).Select(i => new Car { Id = i, Team = i % 2, Demolished = i % 9 == 0 }).ToList();
        var a = cars[3];
        var inline = 0;
        foreach (var b in cars)
        {
            if (b == a || b.Team == a.Team || b.Demolished) continue;
            if (b.Id % 3 != 0) inline |= 1 << (b.Id & 31);
        }
        var mask = IdMask32.Collect(cars, a, static (a, b) => b != a && b.Team != a.Team && !b.Demolished && b.Id % 3 != 0, static b => b.Id);
        mask.Should().Be(inline);
        IdMask32.Has(mask, 34).Should().Be((inline & (1 << 2)) != 0, "ids wrap at 32");
        IdMask32.With(0, 5).Should().Be(32);
        IdMask32.Without(IdMask32.With(0, 5), 5).Should().Be(0);
    }

    [Fact]
    public void OverlapLatch_reports_fresh_entries_once_per_step()
    {
        var latch = new OverlapLatch<string>();
        latch.Begin();
        latch.Record("a").Should().BeTrue();
        latch.Record("a").Should().BeFalse();
        latch.Record("b").Should().BeTrue();
        latch.Entered.Should().Equal("a", "b");
        latch.Begin();
        latch.Record("b").Should().BeFalse();
        latch.Record("c").Should().BeTrue();
        latch.Entered.Should().Equal("c");
        latch.WasOverlapping("a").Should().BeTrue();
        latch.Begin();
        latch.Record("a").Should().BeTrue("a was absent for a step");
    }

    // ── Countdown / TimerSet ───────────────────────────────────────────────

    private sealed class Timers
    {
        public float DashTime, DashCooldown, JumpCooldown, LaunchTimer, FlipTime, ReorientTimer, Coyote, Demolished;
        public bool Grounded;
        public int FlipEnds, Respawns;
    }

    private const float Never = 1e6f;

    private static void InlineTick(Timers v, float dt)
    {
        v.DashTime = MathF.Min(v.DashTime + dt, Never);
        v.DashCooldown = MathF.Max(0, v.DashCooldown - dt);
        v.JumpCooldown = MathF.Max(0, v.JumpCooldown - dt);
        v.LaunchTimer = MathF.Max(0, v.LaunchTimer - dt);
        if (v.FlipTime > 0)
        {
            v.FlipTime = MathF.Max(0, v.FlipTime - dt);
            if (v.FlipTime == 0) v.FlipEnds++;
        }
        v.ReorientTimer = MathF.Max(0, v.ReorientTimer - dt);
        if (!v.Grounded) v.Coyote = MathF.Max(0, v.Coyote - dt);
        if (v.Demolished > 0)
        {
            v.Demolished = MathF.Max(0, v.Demolished - dt);
            if (v.Demolished == 0) v.Respawns++;
        }
    }

    private static void CountdownTick(Timers v, float dt)
    {
        Countdown.CountUp(ref v.DashTime, dt, Never);
        Countdown.Tick(ref v.DashCooldown, dt);
        Countdown.Tick(ref v.JumpCooldown, dt);
        Countdown.Tick(ref v.LaunchTimer, dt);
        if (Countdown.TickRunning(ref v.FlipTime, dt)) v.FlipEnds++;
        Countdown.Tick(ref v.ReorientTimer, dt);
        if (!v.Grounded) Countdown.Tick(ref v.Coyote, dt);
        if (Countdown.IsRunning(v.Demolished) && Countdown.Tick(ref v.Demolished, dt)) v.Respawns++;
    }

    [Fact]
    public void Countdown_reproduces_the_timer_block_bit_for_bit_with_expiry_edges()
    {
        var r = new Random(9);
        var a = new Timers();
        var b = new Timers();
        const float dt = 1f / 60f;
        for (var i = 0; i < 20000; i++)
        {
            if (r.Next(10) == 0)
            {
                var v = (float)r.NextDouble() * 0.4f;
                switch (r.Next(6))
                {
                    case 0: a.DashCooldown = b.DashCooldown = v; break;
                    case 1: a.FlipTime = b.FlipTime = v; break;
                    case 2: a.Demolished = b.Demolished = v; break;
                    case 3: a.Coyote = b.Coyote = v; break;
                    case 4: a.DashTime = b.DashTime = 0; break;
                    default: a.Grounded = b.Grounded = !a.Grounded; break;
                }
            }
            InlineTick(a, dt);
            CountdownTick(b, dt);
            Same(b.DashTime, a.DashTime); Same(b.DashCooldown, a.DashCooldown); Same(b.JumpCooldown, a.JumpCooldown);
            Same(b.LaunchTimer, a.LaunchTimer); Same(b.FlipTime, a.FlipTime); Same(b.ReorientTimer, a.ReorientTimer);
            Same(b.Coyote, a.Coyote); Same(b.Demolished, a.Demolished);
        }
        b.FlipEnds.Should().Be(a.FlipEnds).And.BeGreaterThan(10);
        b.Respawns.Should().Be(a.Respawns).And.BeGreaterThan(10);
    }

    [Fact]
    public void TimerSet_matches_the_cooldown_array_loop()
    {
        var inline = new float[5];
        var set = new TimerSet("a", "b", "c", "d", "e");
        var r = new Random(2);
        const float dt = 1f / 60f;
        for (var i = 0; i < 5000; i++)
        {
            if (r.Next(8) == 0)
            {
                var k = r.Next(5);
                var v = (float)r.NextDouble();
                inline[k] = v;
                set.Start(k, v);
            }
            var before = (float[])inline.Clone();
            for (var k = 0; k < inline.Length; k++) inline[k] = MathF.Max(0, inline[k] - dt);
            set.Tick(dt);
            for (var k = 0; k < 5; k++)
            {
                Same(set[k], inline[k]);
                set.Expired(k).Should().Be(before[k] > 0 && inline[k] == 0);
                set.IsRunning(k).Should().Be(inline[k] > 0);
            }
        }
        set.IndexOf("c").Should().Be(2);
        set.Reset();
        set.Remaining.ToArray().Should().OnlyContain(x => x == 0);
    }

    // ── ButtonEdges / InputLatch / Stick ───────────────────────────────────

    private readonly record struct Frame(float MoveX, float MoveY, int Buttons);

    [Fact]
    public void InputLatch_and_ButtonEdges_reproduce_repeat_last_input_and_pressed_edges()
    {
        var gen = new Random(4);
        float lastX = 0, lastY = 0;
        int lastButtons = 0, prevButtons = 0;
        var latch = new InputLatch<Frame>(default);
        var edges = new ButtonEdges();
        for (var i = 0; i < 5000; i++)
        {
            var inputs = new Dictionary<int, Frame>();
            if (gen.Next(3) != 0) inputs[7] = new Frame((float)gen.NextDouble() * 2 - 1, (float)gen.NextDouble() * 2 - 1, gen.Next(64));

            var input = inputs.TryGetValue(7, out var f) ? f : new Frame(lastX, lastY, lastButtons);
            var pressed = input.Buttons & ~prevButtons;
            prevButtons = input.Buttons;
            lastX = input.MoveX;
            lastY = input.MoveY;
            lastButtons = input.Buttons;

            var resolved = latch.Resolve(inputs, 7);
            var pressed2 = edges.Update(resolved.Buttons);
            resolved.Should().Be(input);
            pressed2.Should().Be(pressed);
            edges.Held.Should().Be(prevButtons);
            edges.WasPressed(1).Should().Be((pressed & 1) != 0);
        }
    }

    [Fact]
    public void Stick_queries_match_the_inline_comparisons()
    {
        foreach (var v in Vectors(2000, 1.5f))
        {
            Stick.AxisBeyond(v.X, 0.3f).Should().Be(MathF.Abs(v.X) > 0.3f);
            Stick.InsideDeadzone(v.X, v.Y, 0.35f).Should().Be(MathF.Sqrt(v.X * v.X + v.Y * v.Y) < 0.35f);
            Stick.Beyond(v.X, v.Y, 0.35f).Should().Be(!Stick.InsideDeadzone(v.X, v.Y, 0.35f));
        }
    }

    // ── EntityRegistry ─────────────────────────────────────────────────────

    [Fact]
    public void EntityRegistry_keeps_the_sorted_list_order_with_fast_lookup()
    {
        var reference = new List<(int Id, string Name)>();
        var reg = new EntityRegistry<(int Id, string Name)>();
        var r = new Random(8);
        for (var i = 0; i < 3000; i++)
        {
            var id = r.Next(60);
            if (r.Next(3) == 0)
            {
                reference.RemoveAll(e => e.Id == id);
                reg.Remove(id);
            }
            else if (reference.All(e => e.Id != id))
            {
                var item = (id, $"e{id}");
                reference.Add(item);
                reference.Sort((a, b) => a.Id.CompareTo(b.Id));
                reg.Add(id, item);
            }
            reg.Should().Equal(reference);
            reg.Ids.Should().Equal(reference.Select(e => e.Id));
            var probe = r.Next(60);
            reg.IndexOf(probe).Should().Be(reference.FindIndex(e => e.Id == probe));
            reg.TryGet(probe, out var found).Should().Be(reference.Any(e => e.Id == probe));
            if (reg.Contains(probe)) found.Id.Should().Be(probe);
        }
        reg.Count.Should().BeGreaterThan(0);
        var dup = () => reg.Add(reg.Ids[0], default);
        dup.Should().Throw<InvalidOperationException>();
    }

    // ── EventSink ──────────────────────────────────────────────────────────

    [Fact]
    public void EventSink_keeps_emission_order_and_reuses_its_buffer()
    {
        var sink = new EventSink<int>();
        static void Emit(IEventSink<int> s, int n) { for (var i = 0; i < n; i++) s.Emit(i); }
        Emit(sink, 5);
        sink.Add(5);
        sink.Should().Equal(0, 1, 2, 3, 4, 5);
        var target = new List<int>();
        sink.DrainTo(target);
        target.Should().Equal(0, 1, 2, 3, 4, 5);
        sink.Count.Should().Be(0);
        Emit(NullEventSink<int>.Instance, 3);

        for (var t = 0; t < 5; t++) { sink.Clear(); Emit(sink, 8); }
        var before = GC.GetAllocatedBytesForCurrentThread();
        var sum = 0;
        for (var t = 0; t < 100; t++)
        {
            sink.Clear();
            Emit(sink, 8);
            foreach (var e in sink) sum += e;
        }
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
        sum.Should().Be(2800);
    }

    // ── StateMachine as the match-phase host ───────────────────────────────

    private sealed class Match : AIContext
    {
        public const float CountdownSeconds = 3f, GoalPause = 1.5f, ReplaySeconds = 2f, Dt = 1f / 60f;
        public float TimeLeft = 10f;
        public bool GoalNow, EveryoneSkipped;
        public readonly List<string> Events = new();
    }

    /// <summary>The inline phase code: phaseTime += dt at the start of the step, the phase logic at
    /// the end; setPhase resets the clock.</summary>
    private sealed class InlinePhases
    {
        public string Phase = "Countdown";
        public float PhaseTime;
        public float TimeLeft = 10f;
        public readonly List<string> Events = new();

        private void SetPhase(string p)
        {
            Phase = p;
            PhaseTime = 0;
        }

        public void Step(bool goalNow, bool everyoneSkipped, Action<float> midStep)
        {
            PhaseTime += Match.Dt;
            midStep(PhaseTime);
            if (Phase == "Countdown")
            {
                if (PhaseTime >= Match.CountdownSeconds)
                {
                    SetPhase("Playing");
                    Events.Add("kickoff");
                }
            }
            else if (Phase == "Playing")
            {
                if (goalNow)
                {
                    SetPhase("Goal");
                    Events.Add("goal");
                    return;
                }
                TimeLeft = MathF.Max(0, TimeLeft - Match.Dt);
                if (TimeLeft <= 0)
                {
                    SetPhase("Ended");
                    Events.Add("end");
                }
            }
            else if (Phase == "Goal" && PhaseTime >= Match.GoalPause)
            {
                if (!everyoneSkipped) SetPhase("Replay");
                else AfterGoal();
            }
            else if (Phase == "Replay" && (PhaseTime >= Match.ReplaySeconds || everyoneSkipped))
            {
                AfterGoal();
            }
        }

        private void AfterGoal()
        {
            if (TimeLeft <= 0)
            {
                SetPhase("Ended");
                Events.Add("end");
            }
            else SetPhase("Countdown");
        }
    }

    private static StateMachine<Match> PhaseMachine()
    {
        static string? AfterGoal(Match m)
        {
            if (m.TimeLeft <= 0)
            {
                m.Events.Add("end");
                return "Ended";
            }
            return "Countdown";
        }
        var b = new StateMachineBuilder<Match>();
        b.ConfigureState("Countdown").AsInitial().Handler((m, _) =>
        {
            if (m.TimeInState < Match.CountdownSeconds) return null;
            m.Events.Add("kickoff");
            return "Playing";
        });
        b.ConfigureState("Playing").Handler((m, dt) =>
        {
            if (m.GoalNow)
            {
                m.Events.Add("goal");
                return "Goal";
            }
            Countdown.Tick(ref m.TimeLeft, dt);
            if (m.TimeLeft > 0) return null;
            m.Events.Add("end");
            return "Ended";
        });
        b.ConfigureState("Goal").Handler((m, _) =>
            m.TimeInState < Match.GoalPause ? null : !m.EveryoneSkipped ? "Replay" : AfterGoal(m));
        b.ConfigureState("Replay").Handler((m, _) =>
            m.TimeInState >= Match.ReplaySeconds || m.EveryoneSkipped ? AfterGoal(m) : null);
        b.ConfigureState("Ended").Handler((_, _) => null);
        return new StateMachine<Match>(b.Build());
    }

    [Fact]
    public void StateMachine_hosts_match_phases_with_the_inline_clock_and_transitions()
    {
        var inline = new InlinePhases();
        var match = new Match();
        var fsm = PhaseMachine();
        fsm.Initialize(match);
        var r = new Random(12);
        var midA = new List<float>();
        var midB = new List<float>();
        for (var t = 0; t < 4000; t++)
        {
            var goal = r.Next(240) == 0;
            var skipped = r.Next(150) == 0;
            inline.Step(goal, skipped, midA.Add);

            match.GoalNow = goal;
            match.EveryoneSkipped = skipped;
            fsm.Advance(match, Match.Dt);
            midB.Add(fsm.TimeInState);       // other work in the step reads the advanced clock
            fsm.RunState(match, Match.Dt);

            fsm.CurrentStateName.Should().Be(inline.Phase, $"tick {t}");
            Same(fsm.TimeInState, inline.PhaseTime);
            Same(match.TimeInState, inline.PhaseTime);
            Same(match.TimeLeft, inline.TimeLeft);
        }
        midB.Should().HaveCount(midA.Count);
        for (var i = 0; i < midA.Count; i++) Same(midB[i], midA[i]);
        match.Events.Should().Equal(inline.Events);
        match.Events.Should().Contain("goal").And.Contain("end");
    }

    [Fact]
    public void StateMachine_transition_to_the_current_state_re_enters_it()
    {
        var enters = 0;
        var exits = 0;
        var b = new StateMachineBuilder<Match>();
        b.ConfigureState("Countdown").AsInitial().Handler((_, _) => "Countdown").OnEnter(_ => enters++).OnExit(_ => exits++);
        var fsm = new StateMachine<Match>(b.Build());
        var m = new Match();
        fsm.Initialize(m);
        fsm.Update(m, 0.5f).Should().BeFalse("returning the current state stays");
        fsm.TimeInState.Should().Be(0.5f);
        fsm.TransitionTo(m, "Countdown");
        fsm.TimeInState.Should().Be(0f);
        m.TimeInState.Should().Be(0f);
        enters.Should().Be(2);
        exits.Should().Be(1);
    }

    [Fact]
    public void StateMachine_update_equals_advance_then_run_state()
    {
        var seen = new List<float>();
        var b = new StateMachineBuilder<Match>();
        b.ConfigureState("A").AsInitial().Duration(1f).Handler((m, _) => { seen.Add(m.TimeInState); return m.TimeInState >= 0.5f ? "B" : null; });
        b.ConfigureState("B").Delay(0.1f).Handler((m, _) => { seen.Add(-m.TimeInState); return null; });
        var def = b.Build();
        var one = new StateMachine<Match>(def);
        var two = new StateMachine<Match>(def);
        var m1 = new Match();
        var m2 = new Match();
        one.Initialize(m1);
        two.Initialize(m2);
        for (var i = 0; i < 80; i++)
        {
            var t1 = one.Update(m1, 1f / 60f);
            two.Advance(m2, 1f / 60f);
            var t2 = two.RunState(m2, 1f / 60f);
            t2.Should().Be(t1);
            two.CurrentStateName.Should().Be(one.CurrentStateName);
            Same(two.TimeInState, one.TimeInState);
            Same(m2.PreviousProgress, m1.PreviousProgress);
        }
        seen.Should().Contain(x => x < 0, "B ran after its delay");
    }
}
