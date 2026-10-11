/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Flow;

/// <summary>
/// Float timers kept in plain fields (cooldowns, windows, grace times): the named form of
/// <c>t = MathF.Max(0, t - dt)</c> and its "just ran out" edge.
///
/// <para>Exactness: <see cref="Tick"/> always computes <c>MathF.Max(0, t - dt)</c> — the same float32
/// expression as the inline form, so a timer ticked here holds the same bits. A timer that is not
/// running (0) stays 0. <see cref="TickRunning"/> only touches a running timer (the
/// <c>if (t &gt; 0) { t = MathF.Max(0, t - dt); if (t == 0) ... }</c> form).</para>
///
/// <para>Choosing: use these helpers for a few timers stored as individual fields on an entity
/// (they keep the field a plain <c>float</c>, cheap to snapshot); use <see cref="TimerSet"/> for
/// an indexed group of timers ticked together (per-slot or per-pad cooldowns) that should report
/// which ones expired this tick. Units are whatever <c>dt</c> is in (normally seconds).</para>
/// </summary>
public static class Countdown
{
    /// <summary>True while the timer has time left (<c>t &gt; 0</c>).</summary>
    public static bool IsRunning(float t) => t > 0;

    /// <summary><c>t = MathF.Max(0, t - dt)</c>; true when it ran out in this tick (was above 0, is 0 now).</summary>
    public static bool Tick(ref float t, float dt)
    {
        var running = t > 0;
        t = MathF.Max(0, t - dt);
        return running && t == 0;
    }

    /// <summary>Ticks only a running timer (<c>t &gt; 0</c>); true when it ran out in this tick.
    /// A timer at or below 0 is left untouched.</summary>
    public static bool TickRunning(ref float t, float dt)
    {
        if (t <= 0) return false;
        t = MathF.Max(0, t - dt);
        return t == 0;
    }

    /// <summary>A count-up clock: <c>t = MathF.Min(t + dt, cap)</c> (time since an event, capped so it
    /// never loses precision or overflows).</summary>
    public static void CountUp(ref float t, float dt, float cap) => t = MathF.Min(t + dt, cap);
}

/// <summary>
/// An indexed set of countdown timers (pad cooldowns, per-slot respawns). <see cref="Tick"/> ticks
/// every timer with <c>MathF.Max(0, t - dt)</c> in index order (exactly the inline loop) and
/// records which ran out in this tick (<see cref="Expired"/>). Optional names map to indices at
/// setup. No allocation per tick.
///
/// <para>Choosing: use this for a fixed, indexable group of timers; for one or two timers kept
/// in fields use the <see cref="Countdown"/> helpers instead.</para>
/// </summary>
public sealed class TimerSet
{
    private readonly float[] _remaining;
    private readonly bool[] _expired;
    private readonly string[]? _names;

    /// <summary><paramref name="count"/> timers, all stopped (0).</summary>
    public TimerSet(int count)
    {
        _remaining = new float[count];
        _expired = new bool[count];
    }

    /// <summary>One named timer per name (index = position).</summary>
    public TimerSet(params string[] names) : this(names.Length)
    {
        _names = (string[])names.Clone();
    }

    /// <summary>Number of timers.</summary>
    public int Count => _remaining.Length;

    /// <summary>The remaining times, writable (index order). Exposed for snapshots and for code
    /// that reads the raw values (<c>Cooldowns[i] &gt; 0</c>).</summary>
    public Span<float> Remaining => _remaining;

    /// <summary>Remaining time of timer <paramref name="index"/>.</summary>
    public float this[int index]
    {
        get => _remaining[index];
        set => _remaining[index] = value;
    }

    /// <summary>Index of the timer named <paramref name="name"/> (throws when unknown).</summary>
    public int IndexOf(string name)
    {
        var i = _names is null ? -1 : Array.IndexOf(_names, name);
        if (i < 0) throw new KeyNotFoundException($"No timer named '{name}'.");
        return i;
    }

    /// <summary>Starts (or restarts) timer <paramref name="index"/> with <paramref name="seconds"/>.</summary>
    public void Start(int index, float seconds) => _remaining[index] = seconds;

    /// <summary>True while timer <paramref name="index"/> has time left.</summary>
    public bool IsRunning(int index) => _remaining[index] > 0;

    /// <summary>True when timer <paramref name="index"/> ran out in the last <see cref="Tick"/>.</summary>
    public bool Expired(int index) => _expired[index];

    /// <summary>Ticks every timer in index order: <c>t = MathF.Max(0, t - dt)</c>.</summary>
    public void Tick(float dt)
    {
        for (var i = 0; i < _remaining.Length; i++)
        {
            var running = _remaining[i] > 0;
            _remaining[i] = MathF.Max(0, _remaining[i] - dt);
            _expired[i] = running && _remaining[i] == 0;
        }
    }

    /// <summary>Stops every timer (all 0, no expiry edges).</summary>
    public void Reset()
    {
        Array.Clear(_remaining);
        Array.Clear(_expired);
    }
}
