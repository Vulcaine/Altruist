using Altruist.Gaming.Rooms;

namespace Tests.Gaming.Rooms;

/// <summary>A generic pad: one axis and a held-buttons bitmask.</summary>
public readonly record struct Pad(float X, int Buttons);

/// <summary>
/// InputBuffer with a declared input model: arrival jitter, starvation, bursts, overflow, stale and
/// duplicate sequences, redundancy, and the press accounting (no press lost, reordered or repeated).
/// </summary>
public class InputBufferTests
{
    // Bits of the test pad: A acts first in a step, B second, C and D share the last stage.
    private const int A = 1, B = 2, C = 4, D = 8, Hold = 16;

    private static readonly RoomInputModel<Pad> Model = RoomInputModel<Pad>.Describe()
        .Buttons(p => p.Buttons, (p, b) => p with { Buttons = b })
        .Action(A, 0)
        .Action(B, 1)
        .Action(C | D, 2)
        .Build();

    private static InputBuffer<Pad> Buffer(InputBufferOptions? o = null) => new(default, Model, o);

    /// <summary>Presses of a stream in order (each input against the previous, in the model's step order).</summary>
    private static List<int> Presses(IEnumerable<Pad> stream, Pad start = default)
    {
        var o = new List<int>();
        var prev = start;
        foreach (var p in stream)
        {
            Model.AppendInOrder(Model.Presses(prev, p), o);
            prev = p;
        }
        return o;
    }

    private static void NoPressTrouble(InputBuffer<Pad> b)
    {
        Assert.Equal(0, b.Stats.PressesLost);
        Assert.Equal(0, b.Stats.PressesReordered);
        Assert.Equal(0, b.Stats.PressesExtra);
    }

    // ------------------------------------------------------------------ model

    [Fact]
    public void Merge_skips_an_input_equal_to_the_one_applied_before()
    {
        Assert.True(Model.TryMerge(new Pad(1, A), new Pad(1, A), new Pad(0, A | C), out var m));
        Assert.Equal(new Pad(0, A | C), m);
    }

    [Fact]
    public void Merge_folds_held_state_changes_without_presses()
    {
        // a moves the axis and lets go of A; b presses C: one step "let go of A, press C" loses nothing.
        Assert.True(Model.TryMerge(new Pad(0, A), new Pad(1, 0), new Pad(0.5f, C), out var m));
        Assert.Equal(new Pad(0.5f, C), m);
    }

    [Fact]
    public void Merge_keeps_a_press_only_when_the_next_input_adds_later_presses()
    {
        Assert.True(Model.TryMerge(default, new Pad(0, A), new Pad(0, A | C), out var m));
        Assert.Equal(new Pad(0, A | C), m);
        // C then D: same stage, one step can't hold both in order.
        Assert.False(Model.TryMerge(default, new Pad(0, C), new Pad(0, C | D), out _));
        // C then A: A would be applied first in a merged step.
        Assert.False(Model.TryMerge(default, new Pad(0, C), new Pad(0, C | A), out _));
        // A pressed then let go (a tap): b alone would lose it.
        Assert.False(Model.TryMerge(default, new Pad(0, A), new Pad(0, 0), out _));
        // A pressed, then the axis moved: the press keeps its own step.
        Assert.False(Model.TryMerge(default, new Pad(0, A), new Pad(1, A), out _));
    }

    [Fact]
    public void Merge_refuses_a_release_and_press_again()
    {
        Assert.False(Model.TryMerge(new Pad(0, C), new Pad(0, 0), new Pad(0, C), out _));
    }

    [Fact]
    public void Held_non_action_bits_are_plain_state()
    {
        Assert.Equal(0, Model.Presses(default, new Pad(0, Hold)));
        Assert.True(Model.TryMerge(default, new Pad(0, Hold), new Pad(0, 0), out _));
    }

    [Fact]
    public void Opaque_inputs_only_merge_when_equal_to_the_applied_one()
    {
        var m = RoomInputModel<Walk>.Opaque;
        Assert.True(m.TryMerge(new Walk(1), new Walk(1), new Walk(2), out var x));
        Assert.Equal(new Walk(2), x);
        Assert.False(m.TryMerge(new Walk(1), new Walk(2), new Walk(2), out _));
        Assert.Equal(0, m.Presses(new Walk(0), new Walk(1)));
    }

    [Fact]
    public void Declaring_a_bit_twice_or_actions_without_buttons_is_an_error()
    {
        Assert.Throws<ArgumentException>(() => RoomInputModel<Pad>.Describe().Buttons(p => p.Buttons, (p, b) => p).Action(A, 0).Action(A, 1).Build());
        Assert.Throws<InvalidOperationException>(() => RoomInputModel<Pad>.Describe().Action(A, 0).Build());
    }

    // ------------------------------------------------------------------ buffer

    [Fact]
    public void Stale_and_duplicate_sequences_are_dropped_and_counted()
    {
        var b = Buffer();
        Assert.True(b.Offer(1, new Pad(0, A)));
        Assert.False(b.Offer(1, new Pad(0, 0)));
        b.Consume();
        Assert.False(b.Offer(1, new Pad(0, 0)));
        Assert.True(b.Offer(3, new Pad(0, 0)));
        Assert.False(b.Offer(2, new Pad(0, 0)));
        // 1 after it was applied: stale; 1 again while queued and 2 after 3: duplicates / out of order.
        Assert.Equal(1, b.Stats.Stale);
        Assert.Equal(2, b.Stats.Duplicates);
    }

    [Fact]
    public void Redundant_packets_add_only_new_inputs()
    {
        var b = Buffer();
        Assert.Equal(3, b.OfferRange(3, new[] { new Pad(0, A), new Pad(0, 0), new Pad(0, C) }));
        Assert.Equal(1, b.OfferRange(4, new[] { new Pad(0, 0), new Pad(0, C), new Pad(0, C | D) }));
        Assert.Equal(4, b.LastQueuedSeq);
        for (var i = 0; i < 4; i++) b.Consume();
        Assert.Equal(3, b.Stats.PressesApplied); // A, C, D
        NoPressTrouble(b);
    }

    [Fact]
    public void A_starved_step_repeats_held_state_and_never_presses_again()
    {
        var b = Buffer(new InputBufferOptions { CatchUp = false });
        b.Offer(1, new Pad(1, C));
        Assert.Equal(new Pad(1, C), b.Consume());
        // Nothing queued: C stays held (no new edge), the axis holds.
        Assert.Equal(new Pad(1, C), b.Consume());
        Assert.Equal(new Pad(1, C), b.Consume());
        Assert.Equal(2, b.Stats.Starved);
        Assert.Equal(1, b.Stats.PressesApplied);
        NoPressTrouble(b);
    }

    [Fact]
    public void A_starved_step_stands_in_for_the_late_input_and_a_matching_input_is_dropped()
    {
        var b = Buffer();
        var held = new Pad(1, 0);
        b.Offer(1, held);
        b.Consume();
        // Two steps without input (a late packet): the held state repeats, standing in for 2 and 3.
        Assert.Equal(held, b.Consume());
        Assert.Equal(held, b.Consume());
        Assert.Equal(3, b.LastAppliedSeq);
        Assert.Equal(2, b.Speculating);
        // They arrive and match what was applied: dropped, as if they had been on time.
        Assert.True(b.Offer(2, held));
        Assert.True(b.Offer(3, held));
        b.Offer(4, new Pad(1, A));
        Assert.Equal(new Pad(1, A), b.Consume());
        Assert.Equal(4, b.LastAppliedSeq);
        Assert.Equal(0, b.Depth);
        Assert.Equal(2, b.Stats.SpeculationHits);
        Assert.False(b.Offer(3, held)); // a duplicate after that is still a duplicate
        NoPressTrouble(b);
    }

    [Fact]
    public void A_late_input_that_differs_is_applied_late_never_lost_or_doubled()
    {
        var b = Buffer();
        b.Offer(1, default);
        b.Consume();
        b.Consume(); // starved: stands in for 2 (a tap of C), which is late
        b.Consume(); // and for 3
        Assert.Equal(3, b.LastAppliedSeq);
        b.Offer(2, new Pad(0, C));
        b.Offer(3, new Pad(0, 0));
        b.Offer(4, new Pad(0, D));
        // The guess for 2 was wrong: 2, 3 and 4 all run, in order, one step each; the ack never goes back.
        Assert.Equal(new Pad(0, C), b.Consume());
        Assert.Equal(3, b.LastAppliedSeq);
        Assert.Equal(new Pad(0, 0), b.Consume());
        Assert.Equal(new Pad(0, D), b.Consume());
        Assert.Equal(4, b.LastAppliedSeq);
        Assert.Equal(0, b.Stats.SpeculationHits);
        Assert.Equal(2, b.Stats.PressesApplied);
        NoPressTrouble(b);
    }

    [Fact]
    public void Speculation_is_bounded_and_off_in_plain_FIFO_mode()
    {
        var b = Buffer(new InputBufferOptions { MaxSpeculation = 3 });
        b.Offer(1, default);
        b.Consume();
        for (var i = 0; i < 10; i++) b.Consume();
        Assert.Equal(4, b.LastAppliedSeq);
        var f = Buffer(new InputBufferOptions { CatchUp = false });
        f.Offer(1, default);
        f.Consume();
        f.Consume();
        Assert.Equal(1, f.LastAppliedSeq);
        Assert.Equal(0, f.Stats.Speculated);
    }

    [Fact]
    public void A_burst_after_a_hitch_drains_to_the_target_without_losing_a_press()
    {
        var b = Buffer(new InputBufferOptions { DrainWindow = 5 });
        var seq = 0;
        // Steady: one input per step.
        for (var i = 0; i < 40; i++)
        {
            b.Offer(++seq, new Pad(1, 0));
            b.Consume();
        }
        Assert.Equal(0, b.Depth);
        // The client hitched and sends 10 inputs at once (no stall here: the server had a cushion
        // of 0, so this is extra latency), then one per step again.
        var stream = new List<Pad>();
        for (var i = 0; i < 10; i++) stream.Add(new Pad(1, i == 3 ? C : i == 6 ? D : 0));
        foreach (var p in stream) b.Offer(++seq, p);
        var depths = new List<int>();
        for (var i = 0; i < 40; i++)
        {
            b.Offer(++seq, new Pad(1, 0));
            b.Consume();
            depths.Add(b.Depth);
        }
        Assert.True(depths[^1] <= 2, $"depth {string.Join(",", depths)}"); // MaxTarget
        Assert.True(b.Stats.Merged >= 8);
        Assert.Equal(2, b.Stats.PressesApplied);
        NoPressTrouble(b);
    }

    [Fact]
    public void Overflow_merges_before_it_drops_and_counts_what_it_drops()
    {
        var b = Buffer(new InputBufferOptions { MaxQueued = 3, CatchUp = false });
        // Held-state changes only: merged, nothing dropped.
        for (var i = 1; i <= 6; i++) b.Offer(i, new Pad(i, 0));
        Assert.Equal(3, b.Depth);
        Assert.Equal(0, b.Stats.Dropped);
        // A tap then a release before D: the release folds into D, the tap survives.
        var d = Buffer(new InputBufferOptions { MaxQueued = 2, CatchUp = false });
        d.Offer(1, new Pad(0, C));
        d.Offer(2, new Pad(0, 0));
        d.Offer(3, new Pad(0, D));
        Assert.Equal(0, d.Stats.Dropped);
        d.Consume();
        d.Consume();
        Assert.Equal(2, d.Stats.PressesApplied);
        NoPressTrouble(d);
        // C tapped twice can't fold (the second press would vanish): the oldest is dropped and counted lost.
        var c = Buffer(new InputBufferOptions { MaxQueued = 2, CatchUp = false });
        c.Offer(1, new Pad(0, C));
        c.Offer(2, new Pad(0, 0));
        c.Offer(3, new Pad(0, C));
        Assert.Equal(1, c.Stats.Dropped);
        c.Consume();
        c.Consume();
        Assert.Equal(1, c.Stats.PressesLost);
    }

    [Fact]
    public void Target_depth_follows_arrival_jitter()
    {
        var b = Buffer();
        var seq = 0;
        for (var i = 0; i < 200; i++)
        {
            b.Offer(++seq, default);
            b.Consume();
        }
        Assert.Equal(0, b.TargetDepth);
        // Bursty arrivals: nothing for 2 steps, then 3 at once.
        for (var i = 0; i < 60; i++)
        {
            if (i % 3 == 2)
                for (var k = 0; k < 3; k++) b.Offer(++seq, default);
            b.Consume();
        }
        Assert.Equal(2, b.TargetDepth);
        Assert.True(b.Jitter >= 2);
    }

    [Fact]
    public void Reset_forgets_everything()
    {
        var b = Buffer();
        b.Offer(5, new Pad(0, A));
        b.Consume();
        b.Reset();
        Assert.Equal(0, b.LastAppliedSeq);
        Assert.True(b.Offer(1, default));
        Assert.Equal(default, b.Consume());
    }

    /// <summary>
    /// Random input streams through random delivery (on time, late, stalls, bursts): every press
    /// offered is applied exactly once, in order, and the applied stream ends where the client's did.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Random_delivery_never_loses_or_reorders_a_press(int seed)
    {
        var rng = new Random(seed);
        var stream = new List<Pad>();
        var cur = new Pad(0, 0);
        for (var i = 0; i < 400; i++)
        {
            var r = rng.NextDouble();
            if (r < 0.15) cur = cur with { Buttons = cur.Buttons ^ (1 << rng.Next(5)) };
            else if (r < 0.25) cur = cur with { X = rng.Next(-2, 3) };
            else if (r < 0.3) cur = cur with { Buttons = rng.Next(32) };
            stream.Add(cur);
        }
        // Only canonical inputs (what a client sends): at most one press per stage, in order.
        var canonical = new List<Pad>();
        var prev = new Pad(0, 0);
        foreach (var p in stream)
        {
            var presses = Model.Presses(prev, p);
            if (!Model.FitsOneStep(presses)) continue;
            canonical.Add(p);
            prev = p;
        }

        var b = Buffer(new InputBufferOptions { MaxQueued = 64, DrainWindow = 1 + rng.Next(30) });
        var applied = new List<Pad>();
        var seq = 0;
        var step = 0;
        while (seq < canonical.Count || b.Depth > 0)
        {
            // Delivery: usually one, sometimes none (late), sometimes a burst.
            var r = rng.NextDouble();
            var n = r < 0.6 ? 1 : r < 0.8 ? 0 : rng.Next(2, 8);
            for (var k = 0; k < n && seq < canonical.Count; k++) b.Offer(++seq, canonical[seq - 1]);
            applied.Add(b.Consume());
            if (++step > 10_000) break;
        }
        Assert.Equal(Presses(canonical), Presses(applied));
        Assert.Equal(canonical[^1], applied[^1]);
        // The last step may have stood in for the client's next input already.
        Assert.InRange(b.LastAppliedSeq, canonical.Count, canonical.Count + 1);
        NoPressTrouble(b);
        Assert.Equal(Presses(canonical).Count, b.Stats.PressesOffered);
        Assert.Equal(b.Stats.PressesOffered, b.Stats.PressesApplied);
    }

    public static IEnumerable<object[]> Seeds() => Enumerable.Range(1, 200).Select(s => new object[] { s });
}
