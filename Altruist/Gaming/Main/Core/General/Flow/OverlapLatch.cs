/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Flow;

/// <summary>
/// A set of small integer ids as the bits of an <c>int</c> (bit = <c>1 &lt;&lt; (id &amp; 31)</c>,
/// so ids 0..31 are distinct and larger ids wrap). Plain <c>int</c> masks keep entity state cheap to
/// copy, snapshot and compare; these helpers name the operations. The TypeScript twin uses the same
/// 32-bit expressions, so masks are equal on both sides.
/// </summary>
public static class IdMask32
{
    /// <summary><c>1 &lt;&lt; (id &amp; 31)</c>.</summary>
    public static int Bit(int id) => 1 << (id & 31);

    /// <summary>True when <paramref name="id"/>'s bit is set.</summary>
    public static bool Has(int mask, int id) => (mask & Bit(id)) != 0;

    /// <summary>The mask with <paramref name="id"/>'s bit set.</summary>
    public static int With(int mask, int id) => mask | Bit(id);

    /// <summary>The mask with <paramref name="id"/>'s bit cleared.</summary>
    public static int Without(int mask, int id) => mask & ~Bit(id);

    /// <summary>
    /// Captures a set at an event: the bits of every item of <paramref name="items"/> (list order)
    /// for which <paramref name="include"/> is true. E.g. "the opponents far enough away when the
    /// dash started", stored on the dashing entity and read later. <paramref name="state"/> is passed
    /// to the delegates so they need not capture (no allocation per call).
    /// </summary>
    public static int Collect<T, TState>(IReadOnlyList<T> items, TState state, Func<TState, T, bool> include, Func<T, int> idOf)
    {
        var mask = 0;
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            if (include(state, item)) mask |= Bit(idOf(item));
        }
        return mask;
    }
}

/// <summary>
/// "Entered this step" detection for overlaps against small integer ids (bit masks, see
/// <see cref="IdMask32"/>). Each step: <see cref="Begin"/>, then <see cref="Record"/> every overlap
/// found (all of them, even after the one you act on, so the next step knows they were already
/// there); <see cref="Record"/> returns true only for a fresh entry (not overlapping last step).
///
/// <para>Exactly the pattern <c>prev = mask; mask = 0; foreach (other) { if (!overlaps) continue;
/// mask |= bit; if (handled || (prev &amp; bit) != 0) continue; ... } entity.Mask = mask;</c>. Only
/// <see cref="Current"/> needs to be stored with the entity (a snapshot / rollback); the previous
/// mask is rebuilt by the next <see cref="Begin"/>. A step that does not run (the entity is
/// skipped) keeps the mask, so the next running step compares against the last one that ran.</para>
/// </summary>
public struct OverlapLatch32
{
    /// <summary>The overlaps recorded in the step before the current one.</summary>
    public int Previous { get; private set; }

    /// <summary>The overlaps recorded so far in the current step (the value to store).</summary>
    public int Current { get; set; }

    /// <summary>A latch continuing from a stored mask.</summary>
    public OverlapLatch32(int current)
    {
        Previous = 0;
        Current = current;
    }

    /// <summary>Starts a step: the current overlaps become the previous ones.</summary>
    public void Begin()
    {
        Previous = Current;
        Current = 0;
    }

    /// <summary>Records an overlap with <paramref name="id"/>; true when it is fresh (was not
    /// overlapping in the previous step).</summary>
    public bool Record(int id)
    {
        var bit = IdMask32.Bit(id);
        Current |= bit;
        return (Previous & bit) == 0;
    }

    /// <summary>True when <paramref name="id"/> was overlapping in the previous step.</summary>
    public readonly bool WasOverlapping(int id) => (Previous & IdMask32.Bit(id)) != 0;

    /// <summary>True when <paramref name="id"/> has been recorded in the current step.</summary>
    public readonly bool IsOverlapping(int id) => (Current & IdMask32.Bit(id)) != 0;
}

/// <summary>
/// "Entered this step" detection for any id type (the general form of <see cref="OverlapLatch32"/>):
/// <see cref="Begin"/> each step, <see cref="Record"/> every overlap (true = fresh). Fresh entries
/// of the step are also listed in record order (<see cref="Entered"/>). No allocation per step once
/// the sets have grown to the working size.
/// </summary>
public sealed class OverlapLatch<TId> where TId : notnull
{
    private HashSet<TId> _previous;
    private HashSet<TId> _current;
    private readonly List<TId> _entered = new();

    /// <summary>Creates an empty latch.</summary>
    /// <param name="comparer">Equality for ids; <c>null</c> uses <see cref="EqualityComparer{T}.Default"/>.</param>
    public OverlapLatch(IEqualityComparer<TId>? comparer = null)
    {
        _previous = new HashSet<TId>(comparer);
        _current = new HashSet<TId>(comparer);
    }

    /// <summary>Fresh entries recorded in the current step, in record order.</summary>
    public IReadOnlyList<TId> Entered => _entered;

    /// <summary>Overlaps recorded so far in the current step.</summary>
    public IReadOnlyCollection<TId> Current => _current;

    /// <summary>Overlaps recorded in the previous step.</summary>
    public IReadOnlyCollection<TId> Previous => _previous;

    /// <summary>Starts a step: the current overlaps become the previous ones.</summary>
    public void Begin()
    {
        (_previous, _current) = (_current, _previous);
        _current.Clear();
        _entered.Clear();
    }

    /// <summary>Records an overlap; true when it is fresh (not overlapping in the previous step).
    /// Recording the same id twice in a step reports it fresh at most once.</summary>
    public bool Record(TId id)
    {
        if (!_current.Add(id)) return false;
        if (_previous.Contains(id)) return false;
        _entered.Add(id);
        return true;
    }

    /// <summary>True when <paramref name="id"/> was overlapping in the previous step.</summary>
    public bool WasOverlapping(TId id) => _previous.Contains(id);

    /// <summary>Forgets everything (both steps).</summary>
    public void Reset()
    {
        _previous.Clear();
        _current.Clear();
        _entered.Clear();
    }
}
