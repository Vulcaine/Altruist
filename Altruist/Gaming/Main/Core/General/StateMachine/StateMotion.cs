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
    public float TimeN { get; set; }
    public float Value { get; set; }
}

/// <summary>
/// Normalized curve used by a combat/state machine to describe motion over the lifetime
/// of a state. Values are consumer-defined distances/offsets that can be sampled by
/// server-side systems or mirrored to clients.
/// </summary>
public sealed class StateMotionCurve
{
    public StateMotionKey[] Keys { get; set; } =
    [
        new StateMotionKey { TimeN = 0f, Value = 0f },
        new StateMotionKey { TimeN = 1f, Value = 0f },
    ];

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

    public float Evaluate(float timeN)
    {
        if (Keys == null || Keys.Length == 0)
            return 0f;

        timeN = Math.Clamp(timeN, 0f, 1f);
        var ordered = Keys.OrderBy(k => k.TimeN).ToArray();
        if (timeN <= ordered[0].TimeN)
            return ordered[0].Value;

        for (int i = 1; i < ordered.Length; i++)
        {
            if (timeN > ordered[i].TimeN)
                continue;

            float span = MathF.Max(0.0001f, ordered[i].TimeN - ordered[i - 1].TimeN);
            float t = (timeN - ordered[i - 1].TimeN) / span;
            return ordered[i - 1].Value + ((ordered[i].Value - ordered[i - 1].Value) * t);
        }

        return ordered[^1].Value;
    }

    public bool HasNonZeroValue(float epsilon = 0.0001f)
        => Keys != null && Keys.Any(k => MathF.Abs(k.Value) > epsilon);
}

/// <summary>
/// First-class Altruist state-machine motion payload. Can be attached to any state
/// and sampled by combat/locomotion code to drive damping and authored motion curves.
/// </summary>
public sealed class StateMotionProfile
{
    public float MovementThrottle { get; set; } = 1f;
    public float MotionScale { get; set; } = 1f;
    public StateMotionCurve X { get; set; } = new();
    public StateMotionCurve Y { get; set; } = new();
    public StateMotionCurve Z { get; set; } = new();

    public bool HasMotion
        => (X?.HasNonZeroValue() ?? false)
        || (Y?.HasNonZeroValue() ?? false)
        || (Z?.HasNonZeroValue() ?? false);

    public StateMotionProfile Clone()
        => new()
        {
            MovementThrottle = MovementThrottle,
            MotionScale = MotionScale,
            X = X?.Clone() ?? new StateMotionCurve(),
            Y = Y?.Clone() ?? new StateMotionCurve(),
            Z = Z?.Clone() ?? new StateMotionCurve(),
        };

    public Vector3 Evaluate(float timeN)
    {
        float scale = MathF.Max(0f, MotionScale);
        return new Vector3(
            (X?.Evaluate(timeN) ?? 0f) * scale,
            (Y?.Evaluate(timeN) ?? 0f) * scale,
            (Z?.Evaluate(timeN) ?? 0f) * scale);
    }

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
