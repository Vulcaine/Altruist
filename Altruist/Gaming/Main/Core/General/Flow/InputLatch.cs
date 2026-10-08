/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.TwoD.Numerics;

namespace Altruist.Gaming.Flow;

/// <summary>
/// Button edges of a held-buttons bitmask, per tick: <see cref="Update"/> takes this tick's held
/// buttons and returns the ones pressed this tick (<c>buttons &amp; ~previous</c>), keeping the
/// held mask for the next tick. Store <see cref="Held"/> with the entity (snapshot / rollback).
/// </summary>
public struct ButtonEdges
{
    /// <summary>The buttons held after the last update (the "previous" of the next one).</summary>
    public int Held { get; set; }

    /// <summary>The buttons pressed in the last update.</summary>
    public int Pressed { get; private set; }

    /// <summary>The buttons released in the last update.</summary>
    public int Released { get; private set; }

    /// <summary>Edges continuing from a stored held mask.</summary>
    public ButtonEdges(int held)
    {
        Held = held;
        Pressed = 0;
        Released = 0;
    }

    /// <summary>Takes this tick's held buttons; returns the pressed ones (<c>buttons &amp; ~previous</c>).</summary>
    public int Update(int buttons)
    {
        var prev = Held;
        Pressed = buttons & ~prev;
        Released = prev & ~buttons;
        Held = buttons;
        return Pressed;
    }

    /// <summary>True when any of <paramref name="mask"/> was pressed in the last update.</summary>
    public readonly bool WasPressed(int mask) => (Pressed & mask) != 0;

    /// <summary>True when any of <paramref name="mask"/> is held.</summary>
    public readonly bool IsHeld(int mask) => (Held & mask) != 0;

    /// <summary>True when any of <paramref name="mask"/> was released in the last update.</summary>
    public readonly bool WasReleased(int mask) => (Released & mask) != 0;
}

/// <summary>
/// The input of a tick for one participant: the input that arrived, or a repeat of the last one
/// when none did (a starved tick, or a remote player being predicted). The result becomes the new
/// "last" input. Exactly <c>input = inputs.TryGetValue(id, out var f) ? f : last; last = input;</c>.
/// </summary>
public sealed class InputLatch<T>
{
    /// <summary>The last resolved input (the neutral input until the first one).</summary>
    public T Last { get; set; }

    /// <summary>A latch whose first repeat is <paramref name="neutral"/>.</summary>
    public InputLatch(T neutral) => Last = neutral;

    /// <summary>The arrived input when <paramref name="arrived"/> is true, else the last one; stored as last.</summary>
    public T Resolve(bool arrived, T input)
    {
        var resolved = arrived ? input : Last;
        Last = resolved;
        return resolved;
    }

    /// <summary>The input for <paramref name="key"/> in this tick's inputs, else the last one; stored as last.</summary>
    public T Resolve<TKey>(IReadOnlyDictionary<TKey, T> inputs, TKey key)
        => Resolve(inputs.TryGetValue(key, out var input), input!);
}

/// <summary>Stick deadzone queries, the named forms of the inline comparisons (same float32 expressions).</summary>
public static class Stick
{
    /// <summary><c>MathF.Abs(axis) &gt; deadzone</c>: one axis pushed past the deadzone.</summary>
    public static bool AxisBeyond(float axis, float deadzone) => MathF.Abs(axis) > deadzone;

    /// <summary><c>VectorMath2D.Length(x, y) &lt; deadzone</c>: the stick inside a round deadzone.</summary>
    public static bool InsideDeadzone(float x, float y, float deadzone) => VectorMath2D.Length(x, y) < deadzone;

    /// <summary><c>VectorMath2D.Length(x, y) &gt;= deadzone</c>: the stick out of a round deadzone.</summary>
    public static bool Beyond(float x, float y, float deadzone) => VectorMath2D.Length(x, y) >= deadzone;
}
