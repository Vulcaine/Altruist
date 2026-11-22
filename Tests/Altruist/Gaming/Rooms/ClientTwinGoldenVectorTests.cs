using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Altruist.Gaming;
using Altruist.Gaming.Rooms;
using Altruist.Numerics;

namespace Tests.Gaming.Rooms;

/// <summary>A generic pad for the golden vectors: one axis and a held-buttons bitmask.</summary>
public readonly record struct GoldenPad(float X, int Buttons);

/// <summary>
/// Cross-language golden vectors: the C# <see cref="RoomInputModel{TInput}"/>, <see cref="InputBuffer{TInput}"/>
/// and <see cref="FixedStepClock"/> run seeded scenarios and the results are committed as JSON next to
/// their TypeScript twins (<c>Clients/TypeScript/input/tests/golden</c>, <c>Clients/TypeScript/prediction/tests/golden</c>),
/// whose tests replay the same scenarios and must produce identical results.
/// <para>These tests fail when a committed file is stale (the C# behaviour changed). Regenerate with
/// <c>ALTRUIST_WRITE_GOLDEN=1 dotnet test --filter ClientTwinGoldenVectorTests</c>, then run the
/// TypeScript tests (<c>npm test</c> in both packages) to see whether the twins still agree.</para>
/// </summary>
public class ClientTwinGoldenVectorTests
{
    private const string InputDir = "Clients/TypeScript/input/tests/golden";
    private const string PredictionDir = "Clients/TypeScript/prediction/tests/golden";

    [Fact]
    public void RoomInputModel_vectors_are_current() => Check($"{InputDir}/room-input-model.json", ModelVectors());

    [Fact]
    public void InputBuffer_vectors_are_current() => Check($"{InputDir}/input-buffer.json", BufferVectors());

    [Fact]
    public void FixedStepClock_vectors_are_current() => Check($"{PredictionDir}/fixed-step-clock.json", ClockVectors());

    [Fact]
    public void Vectors_are_deterministic()
    {
        Assert.Equal(ModelVectors(), ModelVectors());
        Assert.Equal(BufferVectors(), BufferVectors());
        Assert.Equal(ClockVectors(), ClockVectors());
    }

    // ------------------------------------------------------------------ RoomInputModel

    private sealed record ModelCase(string Name, (int Mask, int Stage)[] Actions, bool Buttons, bool StarvedClearsAxis, int[]? Bits = null);

    private static readonly int[] LowBits = { 1, 2, 4, 8, 16 };

    private static readonly ModelCase[] Models =
    {
        new("three-stages", new[] { (1, 0), (2, 1), (4 | 8, 2) }, true, false),
        new("ties-and-gaps", new[] { (8, 3), (16, 0), (2 | 4, 3) }, true, true),
        new("held-only", Array.Empty<(int, int)>(), true, false),
        new("opaque", Array.Empty<(int, int)>(), false, false),
        // 32-bit masks: an action on bit 31 and bit 8, and a 3-bit value field (bits 4..6, a direction
        // index say) packed into the button word as held state.
        new("wide-with-value-field", new[] { (1 << 31, 0), (1 << 8, 1), (1 | 2, 2) }, true, false, new[] { 1, 2, 1 << 8, 1 << 31, 16, 32, 64, 1 << 12 }),
    };

    private static RoomInputModel<GoldenPad> Build(ModelCase m)
    {
        if (!m.Buttons) return RoomInputModel<GoldenPad>.Opaque;
        var b = RoomInputModel<GoldenPad>.Describe().Buttons(p => p.Buttons, (p, v) => p with { Buttons = v });
        foreach (var (mask, stage) in m.Actions) b.Action(mask, stage);
        if (m.StarvedClearsAxis) b.WhenStarved(p => p with { X = 0 });
        return b.Build();
    }

    private static readonly float[] Axes = { 0f, 0.5f, -1f };

    private static string ModelVectors()
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"format\": \"altruist.golden.room-input-model/1\",\n  \"models\": [\n");
        for (var mi = 0; mi < Models.Length; mi++)
        {
            var m = Models[mi];
            var model = Build(m);
            var rng = new DeterministicRandom(1000 + mi);
            sb.Append("    {\n      \"name\": \"").Append(m.Name).Append("\",\n");
            sb.Append("      \"buttons\": ").Append(m.Buttons ? "true" : "false").Append(",\n");
            sb.Append("      \"starvedClearsAxis\": ").Append(m.StarvedClearsAxis ? "true" : "false").Append(",\n");
            sb.Append("      \"actions\": [").Append(string.Join(", ", m.Actions.Select(a => $"[{a.Mask}, {a.Stage}]"))).Append("],\n");
            sb.Append("      \"actionMask\": ").Append(model.ActionMask).Append(",\n");
            var order = new List<int>();
            model.AppendInOrder(-1, order);
            sb.Append("      \"order\": [").Append(string.Join(", ", order)).Append("],\n");
            sb.Append("      \"stageOf\": [").Append(string.Join(", ", Enumerable.Range(0, 32).Select(b => model.StageOf(1 << b)))).Append("],\n");
            var masks = Enumerable.Range(0, 32).Select(p => p).Concat(new[] { 1 << 31, (1 << 31) | 1, (1 << 31) | (1 << 8) | 2, (1 << 8) | 3, -1, 0x70 | 1 }).ToArray();
            sb.Append("      \"fitsOneStep\": [").Append(string.Join(", ", masks.Select(p => $"[{p}, {(model.FitsOneStep(p) ? 1 : 0)}]"))).Append("],\n");
            // [prevX, prevB, aX, aB, bX, bB] -> presses(prev,a), presses(a,b), inOrder(presses(prev,a)|presses(a,b)), starved(a), merge
            sb.Append("      \"cases\": [\n");
            const int n = 500;
            for (var i = 0; i < n; i++)
            {
                var bits = m.Bits ?? LowBits;
                var prev = RandomPad(rng, bits);
                var a = rng.Chance(0.15) ? prev : Mutate(rng, prev, bits);
                var b = Mutate(rng, a, bits);
                var pa = model.Presses(prev, a);
                var pb = model.Presses(a, b);
                var ordered = new List<int>();
                model.AppendInOrder(pa | pb, ordered);
                var starved = model.Starved(a);
                var ok = model.TryMerge(prev, a, b, out var merged);
                sb.Append("        [")
                    .Append(F(prev.X)).Append(", ").Append(prev.Buttons).Append(", ")
                    .Append(F(a.X)).Append(", ").Append(a.Buttons).Append(", ")
                    .Append(F(b.X)).Append(", ").Append(b.Buttons).Append(", ")
                    .Append(pa).Append(", ").Append(pb).Append(", [").Append(string.Join(", ", ordered)).Append("], ")
                    .Append(F(starved.X)).Append(", ").Append(starved.Buttons).Append(", ")
                    .Append(ok ? 1 : 0).Append(", ").Append(F(merged.X)).Append(", ").Append(merged.Buttons).Append(']')
                    .Append(i < n - 1 ? ",\n" : "\n");
            }
            sb.Append("      ]\n    }").Append(mi < Models.Length - 1 ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static GoldenPad RandomPad(DeterministicRandom r, int[] bits)
    {
        var x = Axes[(int)(r.Next() * Axes.Length)];
        var buttons = 0;
        foreach (var b in bits)
            if (r.Chance(0.5)) buttons |= b;
        return new GoldenPad(x, buttons);
    }

    /// <summary>A plausible next input: a few button bits flipped, sometimes the axis moved.</summary>
    private static GoldenPad Mutate(DeterministicRandom r, GoldenPad p, int[]? bits = null)
    {
        bits ??= LowBits;
        var buttons = p.Buttons;
        var flips = (int)(r.Next() * 3);
        for (var i = 0; i < flips; i++) buttons ^= bits[(int)(r.Next() * bits.Length)];
        var x = r.Chance(0.3) ? Axes[(int)(r.Next() * Axes.Length)] : p.X;
        return new GoldenPad(x, buttons);
    }

    // ------------------------------------------------------------------ InputBuffer

    private sealed record BufferScenario(
        string Name, int Seed, InputBufferOptions Options, int Steps,
        double ClientRate, int Latency, int Jitter, double Loss, double Duplicate,
        int Redundancy, bool Reorder, int StallEvery, int StallLength, int ResetAt);

    private static readonly BufferScenario[] Scenarios =
    {
        new("steady", 1, new InputBufferOptions(), 300, 1.0, 3, 0, 0, 0, 1, false, 0, 0, -1),
        new("jitter", 2, new InputBufferOptions(), 400, 1.0, 3, 3, 0, 0, 1, false, 0, 0, -1),
        new("stalls-and-bursts", 3, new InputBufferOptions(), 400, 1.0, 2, 1, 0, 0, 1, false, 60, 9, -1),
        new("fast-client-drain", 4, new InputBufferOptions { DrainWindow = 10 }, 400, 1.08, 2, 1, 0, 0, 1, false, 0, 0, -1),
        new("slow-client-speculation", 5, new InputBufferOptions(), 400, 0.9, 2, 1, 0, 0, 1, false, 0, 0, -1),
        new("loss-with-redundancy", 6, new InputBufferOptions(), 400, 1.0, 3, 2, 0.2, 0.05, 3, false, 0, 0, -1),
        new("reordering", 7, new InputBufferOptions(), 400, 1.0, 3, 4, 0.05, 0.05, 2, true, 0, 0, -1),
        new("fifo-no-catch-up", 8, new InputBufferOptions { CatchUp = false, MaxQueued = 6 }, 400, 1.05, 2, 3, 0.1, 0, 1, true, 50, 8, -1),
        new("overflow", 9, new InputBufferOptions { MaxQueued = 4, MaxTarget = 1, DrainWindow = 5, JitterWindow = 20 }, 400, 1.0, 2, 2, 0, 0, 1, false, 40, 14, -1),
        new("tight-speculation-reset", 10, new InputBufferOptions { MinTarget = 1, MaxTarget = 3, MaxSpeculation = 2 }, 400, 0.95, 4, 3, 0.1, 0, 2, true, 70, 6, 200),
    };

    private static string BufferVectors()
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"format\": \"altruist.golden.input-buffer/1\",\n");
        sb.Append("  \"model\": \"three-stages\",\n  \"scenarios\": [\n");
        for (var si = 0; si < Scenarios.Length; si++)
        {
            var sc = Scenarios[si];
            var o = sc.Options;
            sb.Append("    {\n      \"name\": \"").Append(sc.Name).Append("\",\n");
            sb.Append("      \"options\": { \"maxQueued\": ").Append(o.MaxQueued)
                .Append(", \"minTarget\": ").Append(o.MinTarget)
                .Append(", \"maxTarget\": ").Append(o.MaxTarget)
                .Append(", \"jitterWindow\": ").Append(o.JitterWindow)
                .Append(", \"drainWindow\": ").Append(o.DrainWindow)
                .Append(", \"catchUp\": ").Append(o.CatchUp ? "true" : "false")
                .Append(", \"maxSpeculation\": ").Append(o.MaxSpeculation).Append(" },\n");
            sb.Append("      \"ops\": [\n");
            var buffer = new InputBuffer<GoldenPad>(default, Build(Models[0]), o);
            var ops = new List<string>();
            foreach (var op in Script(sc)) ops.Add(Run(buffer, op));
            sb.Append(string.Join(",\n", ops.Select(l => "        " + l))).Append("\n      ],\n");
            var s = buffer.Stats;
            sb.Append("      \"stats\": { \"offered\": ").Append(s.Offered)
                .Append(", \"stale\": ").Append(s.Stale)
                .Append(", \"duplicates\": ").Append(s.Duplicates)
                .Append(", \"applied\": ").Append(s.Applied)
                .Append(", \"starved\": ").Append(s.Starved)
                .Append(", \"speculated\": ").Append(s.Speculated)
                .Append(", \"speculationHits\": ").Append(s.SpeculationHits)
                .Append(", \"merged\": ").Append(s.Merged)
                .Append(", \"dropped\": ").Append(s.Dropped)
                .Append(", \"pressesOffered\": ").Append(s.PressesOffered)
                .Append(", \"pressesApplied\": ").Append(s.PressesApplied)
                .Append(", \"pressesLost\": ").Append(s.PressesLost)
                .Append(", \"pressesReordered\": ").Append(s.PressesReordered)
                .Append(", \"pressesExtra\": ").Append(s.PressesExtra).Append(" }\n");
            sb.Append("    }").Append(si < Scenarios.Length - 1 ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private abstract record Op;
    private sealed record OfferOp(int Seq, GoldenPad Input) : Op;
    private sealed record RangeOp(int LastSeq, GoldenPad[] Inputs) : Op;
    private sealed record ConsumeOp : Op;
    private sealed record ResetOp : Op;

    /// <summary>One op as a JSON line: <c>[kind, args, result, [lastApplied, lastQueued, depth, target, jitter, speculating]]</c>.</summary>
    private static string Run(InputBuffer<GoldenPad> b, Op op)
    {
        string head;
        switch (op)
        {
            case OfferOp o:
                var ok = b.Offer(o.Seq, o.Input);
                head = $"[\"offer\", [{o.Seq}, {Pad(o.Input)}], {(ok ? 1 : 0)}";
                break;
            case RangeOp r:
                var added = b.OfferRange(r.LastSeq, r.Inputs);
                head = $"[\"range\", [{r.LastSeq}, [{string.Join(", ", r.Inputs.Select(Pad))}]], {added}";
                break;
            case ConsumeOp:
                head = $"[\"consume\", [], {Pad(b.Consume())}";
                break;
            default:
                b.Reset();
                head = "[\"reset\", [], 0";
                break;
        }
        return $"{head}, [{b.LastAppliedSeq}, {b.LastQueuedSeq}, {b.Depth}, {b.TargetDepth}, {F(b.Jitter)}, {b.Speculating}]]";
    }

    private static string Pad(GoldenPad p) => $"[{F(p.X)}, {p.Buttons}]";

    /// <summary>
    /// A client producing inputs at <c>ClientRate</c> per server step, sent through a lossy, jittery
    /// link (ordered unless <c>Reorder</c>) with stalls; the server consumes one input per step.
    /// </summary>
    private static IEnumerable<Op> Script(BufferScenario sc)
    {
        var r = new DeterministicRandom(sc.Seed);
        var sent = new List<GoldenPad>();
        var inFlight = new List<(int Arrive, int Order, Op Op)>();
        var order = 0;
        var clientClock = 0.0;
        var pad = default(GoldenPad);
        var lastArrive = 0;
        for (var step = 0; step < sc.Steps; step++)
        {
            if (step == sc.ResetAt)
            {
                inFlight.Clear();
                sent.Clear();
                yield return new ResetOp();
            }
            clientClock += sc.ClientRate;
            while (clientClock >= 1)
            {
                clientClock -= 1;
                pad = Mutate(r, pad);
                sent.Add(pad);
                var seq = sent.Count;
                if (r.Chance(sc.Loss)) continue;
                var stalled = sc.StallEvery > 0 && step % sc.StallEvery >= sc.StallEvery - sc.StallLength;
                var arrive = step + sc.Latency + (sc.Jitter > 0 ? (int)(r.Next() * (sc.Jitter + 1)) : 0);
                if (stalled) arrive = step - step % sc.StallEvery + sc.StallEvery + sc.Latency;
                if (!sc.Reorder) arrive = Math.Max(arrive, lastArrive);
                lastArrive = Math.Max(lastArrive, arrive);
                Op op;
                if (sc.Redundancy > 1)
                {
                    var count = Math.Min(sc.Redundancy, sent.Count);
                    op = new RangeOp(seq, sent.GetRange(sent.Count - count, count).ToArray());
                }
                else op = new OfferOp(seq, pad);
                inFlight.Add((arrive, order++, op));
                if (r.Chance(sc.Duplicate)) inFlight.Add((arrive + (int)(r.Next() * 3), order++, op));
            }
            foreach (var p in inFlight.Where(p => p.Arrive <= step).OrderBy(p => p.Arrive).ThenBy(p => p.Order).ToList())
            {
                inFlight.Remove(p);
                yield return p.Op;
            }
            yield return new ConsumeOp();
        }
    }

    // ------------------------------------------------------------------ FixedStepClock

    private sealed record ClockCase(int Hz, double MaxFrameDelta, int MaxStepsPerFrame, OverrunPolicy Overrun, int Seed);

    private static readonly ClockCase[] Clocks =
    {
        new(60, 0.25, 8, OverrunPolicy.Drop, 1),
        new(60, 0.25, 6, OverrunPolicy.Drop, 2),
        new(30, 0.1, 3, OverrunPolicy.Carry, 3),
        new(60, 0.25, 2, OverrunPolicy.Carry, 4),
        new(120, 0.25, 4, OverrunPolicy.SlowMotion, 5),
        new(144, 0.5, 8, OverrunPolicy.SlowMotion, 6),
        new(50, 0.05, 1, OverrunPolicy.Drop, 7),
        new(7, 1.0, 3, OverrunPolicy.Carry, 8),
    };

    private static string ClockVectors()
    {
        var sb = new StringBuilder();
        sb.Append("{\n  \"format\": \"altruist.golden.fixed-step-clock/1\",\n  \"clocks\": [\n");
        for (var ci = 0; ci < Clocks.Length; ci++)
        {
            var c = Clocks[ci];
            var clock = new FixedStepClock(c.Hz, c.MaxFrameDelta, c.MaxStepsPerFrame, c.Overrun);
            var r = new DeterministicRandom(c.Seed);
            sb.Append("    {\n      \"hz\": ").Append(c.Hz)
                .Append(",\n      \"maxFrameDelta\": ").Append(F(c.MaxFrameDelta))
                .Append(",\n      \"maxStepsPerFrame\": ").Append(c.MaxStepsPerFrame)
                .Append(",\n      \"overrun\": \"").Append(c.Overrun switch { OverrunPolicy.Drop => "drop", OverrunPolicy.Carry => "carry", _ => "slow-motion" })
                .Append("\",\n      \"dt\": ").Append(F(clock.Dt))
                .Append(",\n      \"frames\": [\n");
            // [frameSeconds, steps, clampedSeconds, overrun, accumulator]
            const int n = 300;
            for (var i = 0; i < n; i++)
            {
                var frame = Frame(r);
                var steps = clock.Advance(frame, out var clamped, out var overrun);
                sb.Append("        [").Append(F(frame)).Append(", ").Append(steps).Append(", ").Append(F(clamped))
                    .Append(", ").Append(overrun ? 1 : 0).Append(", ").Append(F(clock.Accumulator)).Append(']')
                    .Append(i < n - 1 ? ",\n" : "\n");
            }
            sb.Append("      ],\n      \"totalSteps\": ").Append(clock.TotalSteps).Append("\n    }")
                .Append(ci < Clocks.Length - 1 ? ",\n" : "\n");
        }
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    /// <summary>A display-like frame time: steady refresh rates with jitter, hitches, zero and negative frames.</summary>
    private static double Frame(DeterministicRandom r)
    {
        var k = r.Next();
        if (k < 0.02) return 0;
        if (k < 0.03) return -r.Next() * 0.01;
        if (k < 0.08) return 0.1 + r.Next() * 0.9; // a hitch
        if (k < 0.4) return 1.0 / 60;
        if (k < 0.55) return 1.0 / 144;
        if (k < 0.65) return 1.0 / 30;
        return 1.0 / 60 + (r.Next() - 0.5) * 0.008;
    }

    // ------------------------------------------------------------------ files

    /// <summary>Shortest round-trip text of a double (what JavaScript's JSON.parse reads back bit for bit).</summary>
    private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);

    private static string F(float v) => ((double)v).ToString("R", CultureInfo.InvariantCulture);

    private static void Check(string relative, string json)
    {
        var path = Path.Combine(RepoRoot(), relative);
        if (Environment.GetEnvironmentVariable("ALTRUIST_WRITE_GOLDEN") == "1")
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, json);
            return;
        }
        Assert.True(File.Exists(path), $"{relative} is missing: run ALTRUIST_WRITE_GOLDEN=1 dotnet test --filter ClientTwinGoldenVectorTests");
        var committed = File.ReadAllText(path).Replace("\r\n", "\n");
        Assert.True(committed == json,
            $"{relative} is stale (the C# behaviour changed): run ALTRUIST_WRITE_GOLDEN=1 dotnet test --filter ClientTwinGoldenVectorTests, then the TypeScript tests");
    }

    private static string RepoRoot([CallerFilePath] string file = "") =>
        Path.GetFullPath(Path.Combine(Path.GetDirectoryName(file)!, "..", "..", "..", ".."));
}
