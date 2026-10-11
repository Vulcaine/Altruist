/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using System.Linq;

namespace Altruist.Gaming;

/// <summary>One key on a normalized [0..1] motion curve.</summary>
public sealed class StateMotionKey
{
    /// <summary>Normalized time of the key (0 = state entry, 1 = end of the state's duration).</summary>
    public float TimeN { get; set; }
    /// <summary>Curve value at <see cref="TimeN"/> (consumer-defined unit).</summary>
    public float Value { get; set; }
}

/// <summary>
/// Normalized curve used by a combat/state machine to describe motion over the lifetime
/// of a state. Values are consumer-defined distances/offsets that can be sampled by
/// server-side systems or mirrored to clients.
/// </summary>
public sealed class StateMotionCurve
{
    /// <summary>The keys, in any order (evaluated in time order). Defaults to a flat zero curve.</summary>
    public StateMotionKey[] Keys { get; set; } =
    [
        new StateMotionKey { TimeN = 0f, Value = 0f },
        new StateMotionKey { TimeN = 1f, Value = 0f },
    ];

    /// <summary>Deep copy (null keys become the default flat curve).</summary>
    public StateMotionCurve Clone()
        => new()
        {
            Keys = Keys?.Select(k => new StateMotionKey
            {
                TimeN = k.TimeN,
                Value = k.Value,
            }).ToArray()
            ??
            [
                new StateMotionKey { TimeN = 0f, Value = 0f },
                new StateMotionKey { TimeN = 1f, Value = 0f },
            ],
        };

    /// <summary>Piecewise-linear sample at normalized time <paramref name="timeN"/> (clamped to 0..1);
    /// holds the first/last key's value outside the keyed range, 0 with no keys. The keys may be in any
    /// order (keys with equal times keep their array order); evaluation does not allocate.</summary>
    public float Evaluate(float timeN)
    {
        if (Keys == null || Keys.Length == 0)
            return 0f;

        timeN = Math.Clamp(timeN, 0f, 1f);
        int first = 0;
        for (int i = 1; i < Keys.Length; i++)
            if (Precedes(i, first)) first = i;
        if (timeN <= Keys[first].TimeN)
            return Keys[first].Value;

        // The first key (in time order, after `first`) not before timeN, and the key right before it.
        int next = -1;
        for (int i = 0; i < Keys.Length; i++)
        {
            if (i == first || timeN > Keys[i].TimeN) continue;
            if (next < 0 || Precedes(i, next)) next = i;
        }

        if (next < 0)
        {
            int last = 0;
            for (int i = 1; i < Keys.Length; i++)
                if (Precedes(last, i)) last = i;
            return Keys[last].Value;
        }

        int prev = first;
        for (int i = 0; i < Keys.Length; i++)
            if (Precedes(i, next) && Precedes(prev, i)) prev = i;

        float span = MathF.Max(0.0001f, Keys[next].TimeN - Keys[prev].TimeN);
        float t = (timeN - Keys[prev].TimeN) / span;
        return Keys[prev].Value + ((Keys[next].Value - Keys[prev].Value) * t);
    }

    // Time order of the keys: by TimeN, ties by array index (the stable order of a sort).
    private bool Precedes(int a, int b)
    {
        int c = Keys[a].TimeN.CompareTo(Keys[b].TimeN);
        return c < 0 || (c == 0 && a < b);
    }

    /// <summary>True if any key's absolute value exceeds <paramref name="epsilon"/>.</summary>
    public bool HasNonZeroValue(float epsilon = 0.0001f)
        => Keys != null && Keys.Any(k => MathF.Abs(k.Value) > epsilon);
}

/// <summary>
/// First-class Altruist state-machine motion payload. Can be attached to any state
/// and sampled by combat/locomotion code to drive damping and authored motion curves.
/// </summary>
public sealed class StateMotionProfile
{
    /// <summary>Multiplier (0..1 by convention) the consumer applies to regular locomotion while in the state;
    /// 1 = unrestricted. The framework stores it only; the movement code decides how to apply it.</summary>
    public float MovementThrottle { get; set; } = 1f;
    /// <summary>Scale applied to all three curves by <see cref="Evaluate"/> (negative treated as 0).</summary>
    public float MotionScale { get; set; } = 1f;
    /// <summary>Curve of the X component.</summary>
    public StateMotionCurve X { get; set; } = new();
    /// <summary>Curve of the Y component.</summary>
    public StateMotionCurve Y { get; set; } = new();
    /// <summary>Curve of the Z component.</summary>
    public StateMotionCurve Z { get; set; } = new();

    /// <summary>True if any curve has a non-zero key.</summary>
    public bool HasMotion
        => (X?.HasNonZeroValue() ?? false)
        || (Y?.HasNonZeroValue() ?? false)
        || (Z?.HasNonZeroValue() ?? false);

    /// <summary>Deep copy.</summary>
    public StateMotionProfile Clone()
        => new()
        {
            MovementThrottle = MovementThrottle,
            MotionScale = MotionScale,
            X = X?.Clone() ?? new StateMotionCurve(),
            Y = Y?.Clone() ?? new StateMotionCurve(),
            Z = Z?.Clone() ?? new StateMotionCurve(),
        };

    /// <summary>Samples the three curves at normalized time <paramref name="timeN"/> and scales them by
    /// <see cref="MotionScale"/>.</summary>
    public Vector3 Evaluate(float timeN)
    {
        float scale = MathF.Max(0f, MotionScale);
        return new Vector3(
            (X?.Evaluate(timeN) ?? 0f) * scale,
            (Y?.Evaluate(timeN) ?? 0f) * scale,
            (Z?.Evaluate(timeN) ?? 0f) * scale);
    }

    /// <summary>A profile with flat zero curves, scale 1 and the given throttle.</summary>
    public static StateMotionProfile Default(float movementThrottle = 1f)
        => new()
        {
            MovementThrottle = movementThrottle,
            MotionScale = 1f,
            X = new StateMotionCurve(),
            Y = new StateMotionCurve(),
            Z = new StateMotionCurve(),
        };
}
