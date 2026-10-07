/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Numerics;

/// <summary>Math layer. A small, fast, seedable xorshift32 generator whose sequence is fixed forever: the
/// same seed gives the same numbers on every runtime, machine and version, and in the TypeScript
/// package (<c>@altruist/sim2d</c>, <c>DeterministicRandom</c>), which produces the identical
/// sequence. Use it wherever runs must replay exactly (authoritative simulations, bots, golden
/// tests); never <see cref="Random"/>, whose algorithm may change between .NET versions.
/// <para>Not cryptographically secure. Not thread-safe (one instance per consumer).</para>
/// <para>Seeding: <c>state = (uint)seed * 0x9E3779B1 ^ 0x2545F491</c> (0 becomes 0x1234567).
/// Step: <c>s ^= s &lt;&lt; 13; s ^= s &gt;&gt; 17; s ^= s &lt;&lt; 5</c>.</para></summary>
public sealed class DeterministicRandom
{
    private uint _state;

    public DeterministicRandom(int seed)
    {
        _state = unchecked((uint)seed * 0x9E3779B1u) ^ 0x2545F491u;
        if (_state == 0) _state = 0x1234567;
    }

    /// <summary>A generator continuing from a saved <see cref="State"/>.</summary>
    public static DeterministicRandom FromState(uint state) => new(0) { State = state };

    /// <summary>The raw generator state, to save and restore (rollback, replays). Never 0:
    /// setting 0 stores 0x1234567, as seeding does.</summary>
    public uint State
    {
        get => _state;
        set => _state = value == 0 ? 0x1234567u : value;
    }

    /// <summary>The next raw 32-bit value (never 0).</summary>
    public uint NextUInt()
    {
        var s = _state;
        s ^= s << 13;
        s ^= s >> 17;
        s ^= s << 5;
        _state = s;
        return s;
    }

    /// <summary>Uniform in [0, 1): <c>NextUInt() / 4294967296.0</c>.</summary>
    public double Next() => NextUInt() / 4294967296.0;

    /// <summary>Uniform in [-1, 1): <c>(float)(Next() * 2 - 1)</c>.</summary>
    public float Signed() => (float)(Next() * 2 - 1);

    /// <summary>Approximately normal, mean 0, standard deviation 1 (the sum of three uniforms):
    /// <c>(float)((Next() + Next() + Next() - 1.5) * 2)</c>, range [-3, 3).</summary>
    public float Gaussian() => (float)((Next() + Next() + Next() - 1.5) * 2);

    /// <summary>A uniform integer in [-n, n] (0 when <paramref name="n"/> ≤ 0, without drawing):
    /// <c>(int)Math.Floor(Next() * (2 * n + 1)) - n</c>.</summary>
    public int Jitter(int n) => n <= 0 ? 0 : (int)Math.Floor(Next() * (2 * n + 1)) - n;

    /// <summary>True with probability <paramref name="p"/>: <c>Next() &lt; p</c> (always draws).</summary>
    public bool Chance(double p) => Next() < p;
}
