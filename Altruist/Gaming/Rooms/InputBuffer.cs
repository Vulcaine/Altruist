/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Rooms;

/// <summary>
/// A player's sequenced inputs between the network and the fixed step: stale or duplicate
/// sequence numbers are dropped, at most <c>max</c> inputs wait (oldest dropped first), and each
/// step consumes one; a starved step repeats the last input (what a predicting client assumes).
/// </summary>
public sealed class InputBuffer<TInput> where TInput : struct
{
    private readonly Queue<(int Seq, TInput Input)> _queue = new();
    private readonly int _max;
    private readonly TInput _neutral;

    public InputBuffer(int max, TInput neutral)
    {
        _max = max;
        _neutral = neutral;
        Last = neutral;
    }

    /// <summary>Sequence of the input applied last (snapshot ack).</summary>
    public int LastAppliedSeq { get; private set; }
    /// <summary>Newest sequence queued.</summary>
    public int LastQueuedSeq { get; private set; }
    /// <summary>The input applied last (repeated while starved).</summary>
    public TInput Last { get; private set; }
    /// <summary>Inputs waiting (snapshot input depth).</summary>
    public int Depth => _queue.Count;

    /// <summary>Queues an input; false when it was stale or a duplicate.</summary>
    public bool Offer(int seq, in TInput input)
    {
        if (seq <= LastAppliedSeq) return false;
        if (_queue.Count > 0 && seq <= LastQueuedSeq) return false;
        _queue.Enqueue((seq, input));
        LastQueuedSeq = seq;
        while (_queue.Count > _max) _queue.Dequeue();
        return true;
    }

    /// <summary>The input for this step: the next queued one, or the last one again.</summary>
    public TInput Consume()
    {
        if (_queue.TryDequeue(out var item))
        {
            LastAppliedSeq = item.Seq;
            Last = item.Input;
        }
        return Last;
    }

    public void Reset()
    {
        _queue.Clear();
        LastAppliedSeq = 0;
        LastQueuedSeq = 0;
        Last = _neutral;
    }
}
