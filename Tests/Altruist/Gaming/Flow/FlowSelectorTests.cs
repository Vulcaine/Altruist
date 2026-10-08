using Altruist.Gaming.Flow;
using Altruist.Numerics;
using FluentAssertions;
using static Tests.Altruist.Numerics.BitExact;

namespace Tests.Altruist.Gaming.Flow;

/// <summary>FirstMatch, ModifierStack and UtilitySelector: order, short-circuit and float semantics,
/// each checked against the inline ladder it replaces (shapes taken from a 2D match simulation:
/// hit multipliers, strike tactics, dash gravity, role decisions).</summary>
public class FlowSelectorTests
{
    // ── FirstMatch ─────────────────────────────────────────────────────────

    private sealed class HitCase
    {
        public float ZoneMult;
        public bool Super, Kick, Perfect, Dash;
        public const float SuperMul = 3.1f, Nose = 1.35f, FlipKick = 1.7f, PerfectMul = 1.45f, DashMul = 1.25f;
    }

    private static readonly FirstMatch<HitCase, float> HitMultiplier = FirstMatch<HitCase, float>.Create()
        .When("super", c => c.Super, HitCase.SuperMul)
        .When("kick", c => c.Kick, c => MathF.Max(c.ZoneMult, HitCase.Nose) * HitCase.FlipKick)
        .When("perfect", c => c.Perfect, c => c.ZoneMult * HitCase.PerfectMul)
        .When("dash", c => c.Dash, c => c.ZoneMult * HitCase.DashMul)
        .Otherwise(c => c.ZoneMult)
        .Build();

    private static float InlineHitMultiplier(HitCase c)
    {
        var mult = c.ZoneMult;
        if (c.Super) mult = HitCase.SuperMul;
        else if (c.Kick) mult = MathF.Max(mult, HitCase.Nose) * HitCase.FlipKick;
        else if (c.Perfect) mult *= HitCase.PerfectMul;
        else if (c.Dash) mult *= HitCase.DashMul;
        return mult;
    }

    [Fact]
    public void FirstMatch_reproduces_the_hit_multiplier_ladder_bit_for_bit()
    {
        var i = 0;
        foreach (var z in Floats(3000, 3f))
        {
            var c = new HitCase { ZoneMult = z, Super = (i & 1) != 0, Kick = (i & 2) != 0, Perfect = (i & 4) != 0, Dash = (i & 8) != 0 };
            Same(HitMultiplier.Evaluate(c), InlineHitMultiplier(c));
            i++;
        }
    }

    [Fact]
    public void FirstMatch_stops_at_the_first_match_and_runs_earlier_predicates_once_in_order()
    {
        var log = new List<string>();
        var fm = FirstMatch<int, string>.Create()
            .When("a", x => { log.Add("a"); return x == 1; }, "A")
            .When("b", x => { log.Add("b"); return x >= 2; }, x => "B" + x)
            .When("c", x => { log.Add("c"); return true; }, "C")
            .Build();
        fm.Evaluate(2).Should().Be("B2");
        log.Should().Equal("a", "b");
        fm.TryEvaluate(0, out var r, out var idx).Should().BeTrue();
        r.Should().Be("C");
        idx.Should().Be(2);
        fm.NameOf(idx).Should().Be("c");
    }

    [Fact]
    public void FirstMatch_without_default_reports_no_match()
    {
        var fm = FirstMatch<int, int>.Create().When("pos", x => x > 0, 1).Build();
        fm.TryEvaluate(-1, out _).Should().BeFalse();
        var act = () => fm.Evaluate(-1);
        act.Should().Throw<InvalidOperationException>();
        fm.HasDefault.Should().BeFalse();
    }

    private sealed class StrikeCase
    {
        public bool FlipPlanned, ResetChainSpot, High, JumpNow, Behind, Near, FlipKickSpot, LiftSpot;
        public float ComboRate;
        public DeterministicRandom Rng = new(1);
    }

    /// <summary>The strike tactic ladder: guards that latch a flag and roll a chance, then fall through.</summary>
    private static string InlineStrike(StrikeCase s)
    {
        if (!s.FlipPlanned && s.ResetChainSpot)
        {
            s.FlipPlanned = true;
            if (s.Rng.Chance(s.ComboRate)) return "resetChain";
        }
        if (s.High) return s.JumpNow ? "jump" : "approach";
        if (!s.Behind && s.Near) return "getBehind";
        if (s.Behind && !s.FlipPlanned && s.FlipKickSpot)
        {
            s.FlipPlanned = true;
            if (s.Rng.Chance(s.ComboRate)) return "flipKick";
        }
        if (s.Behind && s.LiftSpot) return "lift";
        if (!s.Behind) return "goAround";
        return "drive";
    }

    private static readonly FirstMatch<StrikeCase, string> Strike = FirstMatch<StrikeCase, string>.Create()
        .Try("reset-chain", (StrikeCase s, out string r) =>
        {
            r = "resetChain";
            if (s.FlipPlanned || !s.ResetChainSpot) return false;
            s.FlipPlanned = true;
            return s.Rng.Chance(s.ComboRate);
        })
        .When("high", s => s.High, s => s.JumpNow ? "jump" : "approach")
        .When("get-behind", s => !s.Behind && s.Near, "getBehind")
        .Try("flip-kick", (StrikeCase s, out string r) =>
        {
            r = "flipKick";
            if (!s.Behind || s.FlipPlanned || !s.FlipKickSpot) return false;
            s.FlipPlanned = true;
            return s.Rng.Chance(s.ComboRate);
        })
        .When("lift", s => s.Behind && s.LiftSpot, "lift")
        .When("go-around", s => !s.Behind, "goAround")
        .Otherwise("drive")
        .Build();

    [Fact]
    public void FirstMatch_try_rules_reproduce_a_tactic_ladder_with_latches_and_rng_draws()
    {
        var gen = new Random(7);
        var rngA = new DeterministicRandom(42);
        var rngB = new DeterministicRandom(42);
        for (var i = 0; i < 5000; i++)
        {
            var bits = gen.Next();
            StrikeCase Make(DeterministicRandom rng) => new()
            {
                FlipPlanned = (bits & 1) != 0, ResetChainSpot = (bits & 2) != 0, High = (bits & 4) != 0 && (bits & 64) != 0,
                JumpNow = (bits & 8) != 0, Behind = (bits & 16) != 0, Near = (bits & 32) != 0, FlipKickSpot = (bits & 128) != 0,
                LiftSpot = (bits & 256) != 0, ComboRate = (bits % 100) / 100f, Rng = rng,
            };
            var a = Make(rngA);
            var b = Make(rngB);
            Strike.Evaluate(b).Should().Be(InlineStrike(a));
            b.FlipPlanned.Should().Be(a.FlipPlanned);
            rngB.State.Should().Be(rngA.State);
        }
    }

    [Fact]
    public void FirstMatch_actions_run_only_the_first_matching_branch()
    {
        var ran = new List<string>();
        var modes = FirstMatch<int>.Create()
            .When("jump", x => x == 0, _ => ran.Add("jump"))
            .Try("flip", x => { if (x != 1) return false; ran.Add("flip"); return true; })
            .When("drive", x => x < 5, _ => ran.Add("drive"))
            .Otherwise(_ => ran.Add("air"))
            .Build();
        foreach (var x in new[] { 0, 1, 2, 9 }) modes.Run(x);
        ran.Should().Equal("jump", "flip", "drive", "air");
        modes.Run(9, out var idx).Should().BeTrue();
        idx.Should().Be(-1);
        FirstMatch<int>.Create().When("x", _ => false, _ => { }).Build().Run(0).Should().BeFalse();
    }

    [Fact]
    public void Builders_insert_replace_and_remove_by_name()
    {
        var b = FirstMatch<int, string>.Create()
            .When("a", _ => false, "a")
            .When("c", _ => false, "c");
        b.After("a").When("b", _ => false, "b");
        b.Before("a").When("first", x => x == 1, "first");
        b.Replacing("c").When("c2", x => x == 3, "c2");
        b.Names.Should().Equal("first", "a", "b", "c2");
        b.Remove("b");
        var fm = b.Otherwise("none").Build();
        Enumerable.Range(0, fm.Count).Select(fm.NameOf).Should().Equal("first", "a", "c2");
        fm.Evaluate(3).Should().Be("c2");
        fm.Evaluate(1).Should().Be("first");

        var dup = () => FirstMatch<int, int>.Create().When("a", _ => true, 1).When("a", _ => true, 2);
        dup.Should().Throw<InvalidOperationException>();
        var missing = () => FirstMatch<int, int>.Create().Before("nope");
        missing.Should().Throw<KeyNotFoundException>();
        var dangling = () => FirstMatch<int, int>.Create().When("a", _ => true, 1).After("a").Build();
        dangling.Should().Throw<InvalidOperationException>();
    }

    // ── ModifierStack ──────────────────────────────────────────────────────

    private sealed class GravityState
    {
        public float DashTime, StallTime, GroundNy, Speed;
        public bool BounceUsed, StallEnded, Grounded, Held, Dashing, Stalling;
        public float EdgeFlight;
        public const float Duration = 0.18f, DashGravity = 0.15f, StallMul = 0.35f, StallMax = 0.6f, Afterglow = 0.12f, WallGravity = 1.6f, Dt = 1f / 60f;
        public const bool StallEnabled = true;

        public GravityState Clone() => (GravityState)MemberwiseClone();
        public float Drag => 1 - 0.4f * Math.Clamp((Speed - 20f) / 8f, 0f, 1f);
        public float Steep => GroundNy < 0 ? 1 : 1 - GroundNy;
    }

    /// <summary>The dash / stall / afterglow / wall gravity block, inline.</summary>
    private static float InlineGravity(GravityState v)
    {
        float scale = 1;
        if (v.DashTime < GravityState.Duration && !v.BounceUsed)
        {
            scale = GravityState.DashGravity;
        }
        else if (GravityState.StallEnabled && !v.StallEnded && !v.Grounded)
        {
            if (v.Held && v.StallTime < GravityState.StallMax)
            {
                scale = GravityState.StallMul;
                v.StallTime += GravityState.Dt;
            }
            else
            {
                v.StallEnded = true;
            }
        }
        if (!v.Held) v.StallEnded = true;
        var glow = v.DashTime - GravityState.Duration;
        if (!v.Grounded && !v.BounceUsed && glow >= 0 && glow < GravityState.Afterglow)
        {
            var u = glow / GravityState.Afterglow;
            scale = MathF.Min(scale, GravityState.DashGravity + (1 - GravityState.DashGravity) * u * u);
        }
        if ((v.Grounded || v.EdgeFlight > 0) && v.GroundNy < 0.95f)
        {
            var steep = v.Steep;
            scale *= 1 + (GravityState.WallGravity - 1) * MathF.Min(1, steep * 1.5f);
            scale *= 1 - (1 - v.Drag) * MathF.Min(1, steep * 1.5f);
        }
        return scale;
    }

    private static readonly ModifierStack<GravityState> Gravity = ModifierStack<GravityState>.Create()
        .Set("dash", GravityState.DashGravity, when: v => v.Dashing)
        .Else().Set("stall", GravityState.StallMul, when: v => v.Stalling)
        .Min("afterglow", v =>
        {
            var u = (v.DashTime - GravityState.Duration) / GravityState.Afterglow;
            return GravityState.DashGravity + (1 - GravityState.DashGravity) * u * u;
        }, when: v =>
        {
            var glow = v.DashTime - GravityState.Duration;
            return !v.Grounded && !v.BounceUsed && glow >= 0 && glow < GravityState.Afterglow;
        })
        .Mul("wall", v => 1 + (GravityState.WallGravity - 1) * MathF.Min(1, v.Steep * 1.5f), when: OnSteep)
        .Mul("grip", v => 1 - (1 - v.Drag) * MathF.Min(1, v.Steep * 1.5f), when: OnSteep)
        .Build();

    private static bool OnSteep(GravityState v) => (v.Grounded || v.EdgeFlight > 0) && v.GroundNy < 0.95f;

    /// <summary>The same block with the state updates kept as plain code and the value as modifiers.</summary>
    private static float StackGravity(GravityState v)
    {
        v.Dashing = v.DashTime < GravityState.Duration && !v.BounceUsed;
        v.Stalling = false;
        if (!v.Dashing && GravityState.StallEnabled && !v.StallEnded && !v.Grounded)
        {
            if (v.Held && v.StallTime < GravityState.StallMax)
            {
                v.Stalling = true;
                v.StallTime += GravityState.Dt;
            }
            else v.StallEnded = true;
        }
        if (!v.Held) v.StallEnded = true;
        return Gravity.Apply(1f, v);
    }

    [Fact]
    public void ModifierStack_reproduces_the_gravity_block_bit_for_bit()
    {
        var r = new Random(11);
        for (var i = 0; i < 20000; i++)
        {
            var v = new GravityState
            {
                DashTime = (float)r.NextDouble() * 0.5f,
                StallTime = (float)r.NextDouble() * 0.7f,
                GroundNy = (float)(r.NextDouble() * 2 - 1),
                Speed = (float)r.NextDouble() * 35f,
                BounceUsed = r.Next(4) == 0,
                StallEnded = r.Next(3) == 0,
                Grounded = r.Next(2) == 0,
                Held = r.Next(2) == 0,
                EdgeFlight = r.Next(4) == 0 ? 0.2f : 0,
            };
            var w = v.Clone();
            Same(StackGravity(w), InlineGravity(v));
            Same(w.StallTime, v.StallTime);
            w.StallEnded.Should().Be(v.StallEnded);
        }
    }

    [Fact]
    public void ModifierStack_applies_each_op_as_written_in_order()
    {
        var calls = new List<string>();
        var stack = ModifierStack<int>.Create()
            .Add("add", 0.1f)
            .Mul("mul", 3f)
            .Add("skipped", c => { calls.Add("skipped"); return 100f; }, when: c => c > 5)
            .Max("max", c => { calls.Add("max"); return 0.5f; })
            .Min("min", 0.7f)
            .Map("map", (c, x) => x - c)
            .Build();
        var expected = MathF.Min(MathF.Max((0.2f + 0.1f) * 3f, 0.5f), 0.7f) - 1;
        Same(stack.Apply(0.2f, 1), expected);
        calls.Should().Equal("max");
        stack.OpOf(2).Should().Be(ModifierOp.Add);
    }

    [Fact]
    public void ModifierStack_else_chains_take_at_most_one_branch()
    {
        var stack = ModifierStack<int>.Create()
            .Set("a", 1f, when: x => x == 1)
            .Else().Set("b", 2f, when: x => x == 2)
            .Else().Set("c", 3f)
            .Add("after", 10f, when: x => x == 1)
            .Else().Add("after-else", 20f)
            .Build();
        stack.Apply(0f, 1).Should().Be(11f);
        stack.Apply(0f, 2).Should().Be(22f);
        stack.Apply(0f, 7).Should().Be(23f);
        var dangling = () => ModifierStack<int>.Create().Set("a", 1f).Else().Build();
        dangling.Should().Throw<InvalidOperationException>();
    }

    // ── UtilitySelector (role decision) ────────────────────────────────────

    private enum Role { Kickoff, Attack, Defend, Shadow, Retreat, Support, Refill }

    private const float Dt = 1f / 60f;
    private const float PassiveLimit = 6f;

    /// <summary>One decision's inputs plus the agent's state (what a bot computes before scoring).</summary>
    private sealed class Agent
    {
        public bool Forced;
        public bool FirstMan, Threat, OnKeeper, WrongSide, BallOwnHalf, PadReady, SuperCharge, Camping;
        public float Adv, ChallengeMargin, ThreatTime, Noise, RoleHold, Elite;
        public int DashCharges, DecisionTicks;

        public Role Role = Role.Kickoff, PrevRole = Role.Kickoff;
        public int RoleSince, CampTicks, Switches;
        public DeterministicRandom Rng = new(5);
        public float[] U = new float[7];

        public void SetRole(Role r, int now)
        {
            if (r == Role) return;
            if (r != Role.Kickoff && Role != Role.Kickoff) Switches++;
            PrevRole = Role;
            Role = r;
            RoleSince = now;
        }
    }

    /// <summary>The hand-written decision (scores, vetoes, noise, hysteresis, overrides), inline.</summary>
    private static void InlineDecide(Agent a, int now)
    {
        if (a.Forced)
        {
            a.SetRole(Role.Kickoff, now);
            return;
        }
        var u = a.U;
        u[(int)Role.Kickoff] = -9;
        u[(int)Role.Attack] = (a.FirstMan ? 1 : 0.15f) + Math.Clamp((a.Adv - a.ChallengeMargin) * 1.5f, -1.2f, 1f) - (a.WrongSide ? 1.2f : 0);
        u[(int)Role.Defend] = a.Threat ? 1.7f + MathF.Max(0, 1.5f - a.ThreatTime) : 0;
        if (a.Threat && a.OnKeeper) u[(int)Role.Defend] += 1;
        u[(int)Role.Shadow] = !a.WrongSide && a.FirstMan ? 0.75f + (a.BallOwnHalf ? 0.2f : 0) - MathF.Max(0, a.Adv) * 0.5f : 0.2f;
        u[(int)Role.Retreat] = a.WrongSide ? 0.9f + (a.BallOwnHalf ? 0.5f : 0) : 0;
        u[(int)Role.Support] = a.FirstMan ? 0 : 1.05f;
        u[(int)Role.Refill] = a.DashCharges == 0 && a.PadReady && !a.Threat && a.SuperCharge ? 0.85f : a.DashCharges == 0 && a.PadReady && !a.Threat ? 0.5f : 0;
        if (!a.Threat) u[(int)Role.Defend] = float.NegativeInfinity;
        if (!a.WrongSide) u[(int)Role.Retreat] = float.NegativeInfinity;
        if (a.FirstMan) u[(int)Role.Support] = float.NegativeInfinity;
        if (u[(int)Role.Refill] == 0) u[(int)Role.Refill] = float.NegativeInfinity;
        for (var r = 1; r < 7; r++) u[r] += a.Rng.Gaussian() * a.Noise;
        var held = (now - a.RoleSince) * Dt < a.RoleHold;
        if (a.Role != Role.Kickoff) u[(int)a.Role] += held ? 0.6f : 0.3f;
        var best = a.Role == Role.Kickoff ? Role.Attack : a.Role;
        for (var r = 0; r < 7; r++)
            if (u[r] > u[(int)best]) best = (Role)r;
        if (a.Threat && a.ThreatTime < 1.2f && best != Role.Defend && u[(int)Role.Defend] > 1.5f) best = Role.Defend;
        a.CampTicks = a.Camping ? a.CampTicks + a.DecisionTicks : 0;
        if (a.Camping && a.CampTicks * Dt > PassiveLimit * (1 - 0.6f * a.Elite) && best is Role.Shadow or Role.Defend or Role.Retreat or Role.Refill)
            best = a.FirstMan ? Role.Attack : Role.Support;
        if (best != a.Role) a.SetRole(best, now);
    }

    private static UtilitySelector<Role, Agent> BuildRoleSelector() => UtilitySelector<Role, Agent>.Create()
        .Force("kickoff", a => a.Forced, Role.Kickoff)
        .Option(Role.Kickoff, -9f, noisy: false, transient: true)
        .Option(Role.Attack, a => (a.FirstMan ? 1 : 0.15f) + Math.Clamp((a.Adv - a.ChallengeMargin) * 1.5f, -1.2f, 1f) - (a.WrongSide ? 1.2f : 0))
        .Option(Role.Defend, a =>
        {
            var s = a.Threat ? 1.7f + MathF.Max(0, 1.5f - a.ThreatTime) : 0;
            if (a.Threat && a.OnKeeper) s += 1;
            return s;
        }, veto: (a, _) => !a.Threat)
        .Option(Role.Shadow, a => !a.WrongSide && a.FirstMan ? 0.75f + (a.BallOwnHalf ? 0.2f : 0) - MathF.Max(0, a.Adv) * 0.5f : 0.2f)
        .Option(Role.Retreat, a => a.WrongSide ? 0.9f + (a.BallOwnHalf ? 0.5f : 0) : 0, veto: (a, _) => !a.WrongSide)
        .Option(Role.Support, a => a.FirstMan ? 0 : 1.05f, veto: (a, _) => a.FirstMan)
        .Option(Role.Refill, a => a.DashCharges == 0 && a.PadReady && !a.Threat && a.SuperCharge ? 0.85f : a.DashCharges == 0 && a.PadReady && !a.Threat ? 0.5f : 0,
            veto: (_, s) => s == 0)
        .Noise(a => a.Noise)
        .Inertia(a => a.RoleHold, heldBonus: 0.6f, bonus: 0.3f, tickSeconds: Dt)
        .FallbackTo(Role.Attack)
        .StartWith(Role.Kickoff)
        .Override("threat", (a, best, u) => a.Threat && a.ThreatTime < 1.2f && best != Role.Defend && u[Role.Defend] > 1.5f ? Role.Defend : best)
        .Override("passive-guard", (a, best, _) =>
        {
            a.CampTicks = a.Camping ? a.CampTicks + a.DecisionTicks : 0;
            return a.Camping && a.CampTicks * Dt > PassiveLimit * (1 - 0.6f * a.Elite) && best is Role.Shadow or Role.Defend or Role.Retreat or Role.Refill
                ? (a.FirstMan ? Role.Attack : Role.Support)
                : best;
        })
        .Build();

    [Fact]
    public void UtilitySelector_reproduces_the_role_decision_with_the_same_rng_draws()
    {
        var sel = BuildRoleSelector();
        var inline = new Agent { Rng = new DeterministicRandom(99) };
        var mine = new Agent { Rng = new DeterministicRandom(99) };
        var gen = new Random(3);
        var now = 0;
        for (var i = 0; i < 6000; i++)
        {
            var bits = gen.Next();
            void Fill(Agent a)
            {
                a.Forced = i < 3 || (bits % 97) == 0;
                a.FirstMan = (bits & 1) != 0; a.Threat = (bits & 2) != 0; a.OnKeeper = (bits & 4) != 0; a.WrongSide = (bits & 8) != 0;
                a.BallOwnHalf = (bits & 16) != 0; a.PadReady = (bits & 32) != 0; a.SuperCharge = (bits & 64) != 0; a.Camping = (bits & 0x380) != 0;
                a.Adv = ((bits >> 10) % 400 - 200) / 100f; a.ChallengeMargin = 0.2f; a.ThreatTime = ((bits >> 12) % 300) / 100f;
                a.Noise = ((bits >> 3) % 5) * 0.15f; a.RoleHold = 0.8f; a.Elite = (bits % 3) * 0.5f;
                a.DashCharges = (bits >> 20) % 3; a.DecisionTicks = 6;
            }
            Fill(inline);
            Fill(mine);
            InlineDecide(inline, now);

            var best = sel.Decide(mine, mine.Rng, now);
            if (best != mine.Role)
            {
                mine.SetRole(best, now);
                sel.Choose(best, now);
            }
            mine.Role.Should().Be(inline.Role, $"decision {i}");
            mine.RoleSince.Should().Be(inline.RoleSince);
            mine.CampTicks.Should().Be(inline.CampTicks);
            mine.Switches.Should().Be(inline.Switches);
            mine.Rng.State.Should().Be(inline.Rng.State);
            sel.Current.Should().Be(inline.Role);
            sel.Since.Should().Be(inline.RoleSince);
            if (!inline.Forced)
            {
                sel.LastForced.Should().BeNull();
                for (var r = 0; r < 7; r++)
                {
                    // The selector's scores are before the overrides, like the inline array.
                    Same(sel.Scores[r], inline.U[r]);
                }
            }
            else sel.LastForced.Should().Be("kickoff");
            now += 6;
        }
        inline.Switches.Should().BeGreaterThan(100);
    }

    [Fact]
    public void UtilitySelector_draws_noise_for_vetoed_options_too_and_never_picks_them()
    {
        var sel = UtilitySelector<string, int>.Create()
            .Option("a", 0f)
            .Option("b", 5f, veto: (_, _) => true)
            .Option("c", 0.1f)
            .Noise(_ => 10f)
            .Build();
        var rng = new DeterministicRandom(1);
        var reference = new DeterministicRandom(1);
        for (var i = 0; i < 200; i++)
        {
            sel.Decide(0, rng, i).Should().NotBe("b");
            float.IsNegativeInfinity(sel.Scores["b"]).Should().BeTrue();
            reference.Gaussian(); reference.Gaussian(); reference.Gaussian();
            rng.State.Should().Be(reference.State);
        }
    }

    [Fact]
    public void UtilitySelector_ties_keep_the_current_choice_and_choose_tracks_since()
    {
        var sel = UtilitySelector<int, int>.Create().Option(0, 1f).Option(1, 1f).Option(2, 1f).StartWith(1).Build();
        var rng = new DeterministicRandom(1);
        sel.Decide(0, rng, 0).Should().Be(1);
        sel.Choose(1, 5).Should().BeFalse();
        sel.Since.Should().Be(0);
        sel.Choose(2, 7).Should().BeTrue();
        sel.Previous.Should().Be(1);
        sel.Since.Should().Be(7);
        sel.Select(0, rng, 9, out var changed).Should().Be(2);
        changed.Should().BeFalse();
        rng.State.Should().Be(new DeterministicRandom(1).State, "no noise configured: no draws");
    }

    // ── Allocation ─────────────────────────────────────────────────────────

    [Fact]
    public void Selectors_do_not_allocate_per_evaluation()
    {
        var hit = new HitCase { ZoneMult = 1.1f, Perfect = true };
        var grav = new GravityState { DashTime = 0.25f, GroundNy = 0.5f, Grounded = true, Speed = 25 };
        var sel = BuildRoleSelector();
        var agent = new Agent { Noise = 0.3f, RoleHold = 1, DecisionTicks = 6, Adv = 0.4f, Threat = true, ThreatTime = 1 };
        void Run()
        {
            for (var i = 0; i < 100; i++)
            {
                HitMultiplier.Evaluate(hit);
                Gravity.Apply(1f, grav);
                var best = sel.Decide(agent, agent.Rng, i);
                sel.Choose(best, i);
            }
        }
        Run();
        var before = GC.GetAllocatedBytesForCurrentThread();
        Run();
        (GC.GetAllocatedBytesForCurrentThread() - before).Should().Be(0);
    }
}
