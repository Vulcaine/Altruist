/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections;

namespace Altruist.Gaming.Flow;

/// <summary>
/// Where simulation code reports what happened (a jump, a goal, a hit). Injected once instead of a
/// <c>List&lt;TEvent&gt;</c> threaded through every function; a scratch simulation (prediction, a
/// bot's lookahead) passes <see cref="NullEventSink{TEvent}.Instance"/> and pays nothing.
/// </summary>
public interface IEventSink<in TEvent>
{
    /// <summary>Reports an event (kept in emission order).</summary>
    void Emit(TEvent e);
}

/// <summary>
/// Collects events in emission order. <see cref="Add"/> is an alias of <see cref="Emit"/>, so code
/// written against <c>List&lt;TEvent&gt;.Add</c> compiles unchanged after switching the parameter
/// type. Clear it at the start of each tick (or hand the tick's events on with
/// <see cref="DrainTo"/>); the backing list is reused, so a steady tick does not allocate.
/// </summary>
public sealed class EventSink<TEvent> : IEventSink<TEvent>, IReadOnlyList<TEvent>
{
    private readonly List<TEvent> _events;

    public EventSink(int capacity = 16) => _events = new List<TEvent>(capacity);

    /// <summary>Number of events collected.</summary>
    public int Count => _events.Count;

    /// <summary>The event at <paramref name="index"/> (emission order).</summary>
    public TEvent this[int index] => _events[index];

    /// <summary>Reports an event.</summary>
    public void Emit(TEvent e) => _events.Add(e);

    /// <summary>Same as <see cref="Emit"/>.</summary>
    public void Add(TEvent e) => _events.Add(e);

    /// <summary>Forgets the collected events (keeps the capacity).</summary>
    public void Clear() => _events.Clear();

    /// <summary>Appends the collected events to <paramref name="target"/> in order, then clears.</summary>
    public void DrainTo(ICollection<TEvent> target)
    {
        foreach (var e in _events) target.Add(e);
        _events.Clear();
    }

    /// <summary>A copy of the collected events.</summary>
    public List<TEvent> ToList() => new(_events);

    /// <summary>Allocation-free enumerator (emission order).</summary>
    public List<TEvent>.Enumerator GetEnumerator() => _events.GetEnumerator();

    IEnumerator<TEvent> IEnumerable<TEvent>.GetEnumerator() => _events.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => _events.GetEnumerator();
}

/// <summary>A sink that drops every event (scratch simulations).</summary>
public sealed class NullEventSink<TEvent> : IEventSink<TEvent>
{
    /// <summary>The shared instance.</summary>
    public static readonly NullEventSink<TEvent> Instance = new();

    private NullEventSink() { }

    public void Emit(TEvent e) { }
}
