/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Rooms;

/// <summary>Tuning of <see cref="InputBuffer{TInput}"/> (<see cref="RoomHostOptions.Input"/>); the defaults suit 30-120 Hz rooms.</summary>
public sealed record InputBufferOptions
{
    /// <summary>Inputs waiting beyond this are merged (or, when that would lose a press, dropped oldest first).</summary>
    public int MaxQueued { get; init; } = 16;
    /// <summary>Bounds of the adaptive target depth (inputs kept queued against arrival jitter).</summary>
    public int MinTarget { get; init; }
    public int MaxTarget { get; init; } = 2;
    /// <summary>Arrivals the jitter is measured over.</summary>
    public int JitterWindow { get; init; } = 120;
    /// <summary>
    /// Steps a queue must stay above <see cref="MaxTarget"/> before it is drained (a standing queue,
    /// not a burst in flight). Below that the client's own pacing moves the depth toward the target:
    /// draining there would fight it.
    /// </summary>
    public int DrainWindow { get; init; } = 30;
    /// <summary>
    /// Speculate on starved steps and drain standing queues (off: plain FIFO, one input per step,
    /// a starved step repeats the last input without consuming a sequence).
    /// </summary>
    public bool CatchUp { get; init; } = true;
    /// <summary>Starved steps in a row that stand in for the client's next inputs (see <see cref="InputBuffer{TInput}"/>).</summary>
    public int MaxSpeculation { get; init; } = 8;
}

/// <summary>Counters of one buffer (and, summed, of every buffer: <see cref="InputBufferTotals"/>).</summary>
public sealed class InputBufferStats
{
    public long Offered;
    public long Stale;
    public long Duplicates;
    public long Applied;
    /// <summary>Steps with nothing queued (the held state was repeated).</summary>
    public long Starved;
    /// <summary>Starved steps that stood in for the client's next input, and how many of those it then confirmed.</summary>
    public long Speculated;
    public long SpeculationHits;
    /// <summary>Inputs folded into the next one (draining a standing queue, overflow).</summary>
    public long Merged;
    /// <summary>Inputs dropped on overflow because merging would have lost a press.</summary>
    public long Dropped;
    /// <summary>Presses (declared action edges) in the offered stream, and applied to the simulation.</summary>
    public long PressesOffered;
    public long PressesApplied;
    /// <summary>Offered presses that never reached the simulation.</summary>
    public long PressesLost;
    /// <summary>Presses applied in a different order than offered.</summary>
    public long PressesReordered;
    /// <summary>Presses applied that were never offered (a repeated input pressing again).</summary>
    public long PressesExtra;
}

/// <summary>Process-wide sums of every <see cref="InputBuffer{TInput}"/>'s counters (metrics endpoints, load tests).</summary>
public static class InputBufferTotals
{
    private static readonly InputBufferStats _t = new();

    internal static void Add(ref long field, long n) => Interlocked.Add(ref field, n);
    internal static InputBufferStats Live => _t;

    /// <summary>A consistent-enough copy of the sums.</summary>
    public static InputBufferStats Read() => new()
    {
        Offered = Interlocked.Read(ref _t.Offered),
        Stale = Interlocked.Read(ref _t.Stale),
        Duplicates = Interlocked.Read(ref _t.Duplicates),
        Applied = Interlocked.Read(ref _t.Applied),
        Starved = Interlocked.Read(ref _t.Starved),
        Speculated = Interlocked.Read(ref _t.Speculated),
        SpeculationHits = Interlocked.Read(ref _t.SpeculationHits),
        Merged = Interlocked.Read(ref _t.Merged),
        Dropped = Interlocked.Read(ref _t.Dropped),
        PressesOffered = Interlocked.Read(ref _t.PressesOffered),
        PressesApplied = Interlocked.Read(ref _t.PressesApplied),
        PressesLost = Interlocked.Read(ref _t.PressesLost),
        PressesReordered = Interlocked.Read(ref _t.PressesReordered),
        PressesExtra = Interlocked.Read(ref _t.PressesExtra),
    };
}

/// <summary>
/// A player's sequenced inputs between the network and the fixed step. Each step applies one
/// input; the client predicts with the same stream, so the buffer's job is to apply exactly the
/// client's inputs, one per step, as soon as possible:
/// <list type="bullet">
/// <item>stale and duplicate sequence numbers are dropped (redundant packets are fine: <see cref="OfferRange"/>);</item>
/// <item>a step with nothing queued repeats the held state (<see cref="RoomInputModel{TInput}.Starved"/>), never a press;</item>
/// <item>such a step is speculative: it stands in for the client's next sequence and is acked as
/// such (up to <see cref="InputBufferOptions.MaxSpeculation"/> in a row). When that input arrives and
/// equals what was applied, it is dropped: the stall never happened as far as the predicting client
/// can tell. When it differs, it is applied on the next step (late by one, every press kept, in order);</item>
/// <item>a queue that stays above <see cref="InputBufferOptions.MaxTarget"/> for <see cref="InputBufferOptions.DrainWindow"/>
/// steps (a burst after a hitch) is drained the same way, one merge per step;</item>
/// <item>the target depth adapts to the measured arrival jitter (<see cref="TargetDepth"/>, between
/// <see cref="InputBufferOptions.MinTarget"/> and <see cref="InputBufferOptions.MaxTarget"/>): send it
/// to the client with the ack so its input clock keeps that many queued.</item>
/// </list>
/// Not thread-safe: offered and consumed on the engine thread (consumed on the room's worker).
/// </summary>
public sealed class InputBuffer<TInput> where TInput : struct
{
    private readonly LinkedList<(int Seq, TInput Input)> _queue = new();
    private readonly InputBufferOptions _opt;
    private readonly RoomInputModel<TInput> _model;
    private readonly TInput _neutral;
    /// <summary>Arrival offsets (step of arrival minus sequence), a ring over the jitter window.</summary>
    private readonly double[] _offsets;
    private int _offsetCount;
    private int _offsetNext;
    /// <summary>Queue depth after each of the last DrainWindow steps.</summary>
    private readonly int[] _depths;
    private int _depthCount;
    private int _depthNext;
    private long _step;
    /// <summary>Speculative steps whose input has not arrived yet: (sequence they stood in for, input applied).</summary>
    private readonly LinkedList<(int Seq, TInput Input, bool Wrong)> _spec = new();
    /// <summary>The newest offered input (presses of the next one are counted against it).</summary>
    private TInput _lastOffered;
    /// <summary>Offered presses not applied yet, in offered order: (sequence, button bit).</summary>
    private readonly LinkedList<(int Seq, int Bit)> _expected = new();
    private readonly List<int> _scratch = new(4);

    /// <summary>A plain FIFO (no speculation, no draining) of opaque inputs: the pre-0.9.10 behaviour.</summary>
    public InputBuffer(int max, TInput neutral) : this(neutral, null, new InputBufferOptions { MaxQueued = max, CatchUp = false }) { }

    public InputBuffer(TInput neutral, RoomInputModel<TInput>? model = null, InputBufferOptions? options = null)
    {
        _neutral = neutral;
        _model = model ?? RoomInputModel<TInput>.Opaque;
        _opt = options ?? new InputBufferOptions();
        _offsets = new double[Math.Max(2, _opt.JitterWindow)];
        _depths = new int[Math.Max(1, _opt.DrainWindow)];
        Last = neutral;
        _lastOffered = neutral;
        TargetDepth = _opt.MinTarget;
    }

    /// <summary>Sequence of the input applied last (snapshot ack).</summary>
    public int LastAppliedSeq { get; private set; }
    /// <summary>Newest sequence queued.</summary>
    public int LastQueuedSeq { get; private set; }
    /// <summary>The input applied last (repeated while starved).</summary>
    public TInput Last { get; private set; }
    /// <summary>Inputs waiting (snapshot input depth).</summary>
    public int Depth => _queue.Count;
    /// <summary>How many inputs the client should keep queued here (measured arrival jitter, in steps).</summary>
    public int TargetDepth { get; private set; }
    /// <summary>Arrival jitter over the window, in steps (max minus min of arrival time minus sequence).</summary>
    public double Jitter { get; private set; }
    /// <summary>Speculative steps not confirmed or corrected yet.</summary>
    public int Speculating => _spec.Count;
    public InputBufferStats Stats { get; } = new();
    public RoomInputModel<TInput> Model => _model;

    /// <summary>Queues an input; false when it was stale or a duplicate.</summary>
    public bool Offer(int seq, in TInput input)
    {
        if (seq <= LastAppliedSeq && _spec.First is not null && ResolveSpeculation(seq, input)) return true;
        if (seq <= LastAppliedSeq || seq <= LastQueuedSeq)
        {
            if (seq <= LastAppliedSeq) Count(ref Stats.Stale, ref InputBufferTotals.Live.Stale);
            else Count(ref Stats.Duplicates, ref InputBufferTotals.Live.Duplicates);
            return false;
        }
        // Speculations for sequences the client skipped (never on an ordered transport) are given up.
        while (_spec.First is { } stale && stale.Value.Seq < seq) _spec.RemoveFirst();
        Accept(seq, input);
        _queue.AddLast((seq, input));
        LastQueuedSeq = seq;
        while (_queue.Count > _opt.MaxQueued) Shrink();
        return true;
    }

    /// <summary>Counts an accepted input: arrival time, presses as the client made them.</summary>
    private void Accept(int seq, in TInput input)
    {
        Count(ref Stats.Offered, ref InputBufferTotals.Live.Offered);
        RecordArrival(seq);
        // Against the previous input the client sent (inputs arrive in sequence order).
        var presses = _model.Presses(_lastOffered, input);
        _lastOffered = input;
        if (presses == 0) return;
        _scratch.Clear();
        _model.AppendInOrder(presses, _scratch);
        foreach (var b in _scratch) _expected.AddLast((seq, b));
        Count(ref Stats.PressesOffered, ref InputBufferTotals.Live.PressesOffered, _scratch.Count);
    }

    /// <summary>The input a speculative step stood in for arrived: confirm it, or queue it to apply late.</summary>
    private bool ResolveSpeculation(int seq, in TInput input)
    {
        // Older speculations whose input never came (cannot happen on an ordered transport) are given up.
        while (_spec.First is { } old && old.Value.Seq < seq) _spec.RemoveFirst();
        if (_spec.First is not { } node || node.Value.Seq != seq) return false;
        var (_, guess, wrong) = node.Value;
        _spec.RemoveFirst();
        Accept(seq, input);
        LastQueuedSeq = Math.Max(LastQueuedSeq, seq);
        if (!wrong && EqualityComparer<TInput>.Default.Equals(guess, input))
        {
            Count(ref Stats.SpeculationHits, ref InputBufferTotals.Live.SpeculationHits);
            return true;
        }
        // Wrong guess: the real input runs on a step of its own (late, like a plain starved step),
        // ahead of the inputs queued after it; the ack stays. The speculations after it were built
        // on the wrong guess: their inputs are applied for real too when they arrive.
        for (var n = _spec.First; n is not null; n = n.Next) n.Value = n.Value with { Wrong = true };
        var at = _queue.First;
        while (at is not null && at.Value.Seq < seq) at = at.Next;
        if (at is null) _queue.AddLast((seq, input));
        else _queue.AddBefore(at, (seq, input));
        return true;
    }

    /// <summary>
    /// Queues a run of inputs ending at <paramref name="lastSeq"/> (oldest first): a packet that
    /// repeats the last few inputs for redundancy. Already known ones are skipped; returns how many were new.
    /// </summary>
    public int OfferRange(int lastSeq, ReadOnlySpan<TInput> inputs)
    {
        var added = 0;
        for (var i = 0; i < inputs.Length; i++)
        {
            var seq = lastSeq - (inputs.Length - 1 - i);
            if (seq > LastQueuedSeq && seq > LastAppliedSeq && Offer(seq, inputs[i])) added++;
        }
        return added;
    }

    /// <summary>The input for this step: the next queued one (merged when catching up), or the held state again.</summary>
    public TInput Consume()
    {
        _step++;
        var prev = Last;
        if (_queue.First is null)
        {
            Count(ref Stats.Starved, ref InputBufferTotals.Live.Starved);
            Last = _model.Starved(prev);
            // Stand in for the client's next input (acked as applied; checked when it arrives).
            if (_opt.CatchUp && _spec.Count < _opt.MaxSpeculation && LastAppliedSeq > 0)
            {
                LastAppliedSeq++;
                _spec.AddLast((LastAppliedSeq, Last, false));
                Count(ref Stats.Speculated, ref InputBufferTotals.Live.Speculated);
            }
            RecordDepth();
            Track(prev, Last, int.MinValue);
            return Last;
        }
        var item = _queue.First.Value;
        _queue.RemoveFirst();
        // A standing queue (above the largest target for a whole window): one fold per step.
        if (_opt.CatchUp && _queue.Count > _opt.MaxTarget && StandingAbove(_opt.MaxTarget)) TryFold(prev, ref item);
        Count(ref Stats.Applied, ref InputBufferTotals.Live.Applied);
        LastAppliedSeq = Math.Max(LastAppliedSeq, item.Seq);
        Last = item.Input;
        RecordDepth();
        Track(prev, Last, item.Seq);
        return Last;
    }

    public void Reset()
    {
        _queue.Clear();
        _expected.Clear();
        LastAppliedSeq = 0;
        LastQueuedSeq = 0;
        Last = _neutral;
        _lastOffered = _neutral;
        _spec.Clear();
        _offsetCount = _offsetNext = 0;
        _depthCount = _depthNext = 0;
        Jitter = 0;
        TargetDepth = _opt.MinTarget;
    }

    // ------------------------------------------------------------------ catching up

    /// <summary>Merges <paramref name="item"/> with the next queued input when no press is lost.</summary>
    private bool TryFold(in TInput prev, ref (int Seq, TInput Input) item)
    {
        var next = _queue.First;
        if (next is null || !_model.TryMerge(prev, item.Input, next.Value.Input, out var merged)) return false;
        item = (next.Value.Seq, merged);
        _queue.RemoveFirst();
        Count(ref Stats.Merged, ref InputBufferTotals.Live.Merged);
        return true;
    }

    /// <summary>Overflow: merge the first mergeable pair, else drop the oldest input.</summary>
    private void Shrink()
    {
        var prev = Last;
        for (var node = _queue.First; node?.Next is { } next; node = next)
        {
            if (_model.TryMerge(prev, node.Value.Input, next.Value.Input, out var merged))
            {
                next.Value = (next.Value.Seq, merged);
                _queue.Remove(node);
                Count(ref Stats.Merged, ref InputBufferTotals.Live.Merged);
                return;
            }
            prev = node.Value.Input;
        }
        _queue.RemoveFirst();
        Count(ref Stats.Dropped, ref InputBufferTotals.Live.Dropped);
    }

    private bool StandingAbove(int target)
    {
        if (_depthCount < _depths.Length) return false;
        for (var i = 0; i < _depthCount; i++)
            if (_depths[i] <= target) return false;
        return true;
    }

    private void RecordDepth()
    {
        _depths[_depthNext] = _queue.Count;
        _depthNext = (_depthNext + 1) % _depths.Length;
        if (_depthCount < _depths.Length) _depthCount++;
    }

    // ------------------------------------------------------------------ jitter

    private void RecordArrival(int seq)
    {
        _offsets[_offsetNext] = _step - seq;
        _offsetNext = (_offsetNext + 1) % _offsets.Length;
        if (_offsetCount < _offsets.Length) _offsetCount++;
        double min = double.MaxValue, max = double.MinValue;
        for (var i = 0; i < _offsetCount; i++)
        {
            var o = _offsets[i];
            if (o < min) min = o;
            if (o > max) max = o;
        }
        Jitter = max - min;
        TargetDepth = Math.Clamp((int)Math.Ceiling(Jitter - 0.5), _opt.MinTarget, _opt.MaxTarget);
    }

    // ------------------------------------------------------------------ press accounting

    /// <summary>Matches the presses this step applied against the offered ones (lost, reordered, extra).</summary>
    private void Track(in TInput prev, in TInput applied, int appliedSeq)
    {
        var presses = _model.Presses(prev, applied);
        if (presses != 0)
        {
            _scratch.Clear();
            _model.AppendInOrder(presses, _scratch);
            Count(ref Stats.PressesApplied, ref InputBufferTotals.Live.PressesApplied, _scratch.Count);
            foreach (var b in _scratch)
            {
                var head = _expected.First;
                if (head is not null && head.Value.Bit == b)
                {
                    _expected.RemoveFirst();
                    continue;
                }
                var found = head;
                while (found is not null && found.Value.Bit != b) found = found.Next;
                if (found is null) Count(ref Stats.PressesExtra, ref InputBufferTotals.Live.PressesExtra);
                else
                {
                    _expected.Remove(found);
                    Count(ref Stats.PressesReordered, ref InputBufferTotals.Live.PressesReordered);
                }
            }
        }
        // Offered presses of inputs already applied (or skipped) that never showed: lost.
        while (_expected.First is { } e && e.Value.Seq <= appliedSeq)
        {
            _expected.RemoveFirst();
            Count(ref Stats.PressesLost, ref InputBufferTotals.Live.PressesLost);
        }
    }

    private static void Count(ref long own, ref long total, long n = 1)
    {
        own += n;
        InputBufferTotals.Add(ref total, n);
    }
}
