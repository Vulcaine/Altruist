/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming;

/// <summary>What a fixed-step clock does when a frame needs more steps than <c>max-steps-per-frame</c>.</summary>
public enum OverrunPolicy
{
    /// <summary>The time left over after the last allowed step is discarded (the accumulator restarts at 0).</summary>
    Drop,

    /// <summary>The backlog is kept and stepped in the following frames (bounded by <c>max-frame-delta</c>).</summary>
    Carry,

    /// <summary>
    /// A frame never adds more than <c>max-steps-per-frame</c> steps of time, so an overloaded server
    /// runs slower than real time without a backlog; the sub-step remainder is kept, so step timing stays even.
    /// </summary>
    SlowMotion,
}

/// <summary>
/// Fixed-timestep accumulator: turns variable frame times into a whole number of equal steps.
/// The frame time is clamped to <see cref="MaxFrameDelta"/>, added to a double-precision
/// accumulator, and every whole <see cref="Dt"/> in it is one step, at most
/// <see cref="MaxStepsPerFrame"/> per frame. Pure arithmetic, no clock of its own: the same frame
/// times always produce the same steps.
/// </summary>
public sealed class FixedStepClock
{
    private readonly double _dt;
    private double _accumulator;

    /// <summary>Creates a clock of <paramref name="hz"/> steps per second.</summary>
    /// <param name="hz">Steps per second (positive).</param>
    /// <param name="maxFrameDelta">Longest frame time (seconds) counted per <see cref="Advance"/>; longer frames are clamped.</param>
    /// <param name="maxStepsPerFrame">Most steps one frame may produce.</param>
    /// <param name="overrun">What happens to time beyond <paramref name="maxStepsPerFrame"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A non-positive argument.</exception>
    public FixedStepClock(int hz, double maxFrameDelta = 0.25, int maxStepsPerFrame = 8, OverrunPolicy overrun = OverrunPolicy.Drop)
    {
        if (hz <= 0)
            throw new ArgumentOutOfRangeException(nameof(hz), hz, "The fixed step rate must be positive.");
        if (maxFrameDelta <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxFrameDelta), maxFrameDelta, "max-frame-delta must be positive.");
        if (maxStepsPerFrame <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxStepsPerFrame), maxStepsPerFrame, "max-steps-per-frame must be positive.");

        Hz = hz;
        Dt = 1f / hz;
        _dt = Dt;
        MaxFrameDelta = maxFrameDelta;
        MaxStepsPerFrame = maxStepsPerFrame;
        Overrun = overrun;
    }

    /// <summary>Steps per second.</summary>
    public int Hz { get; }

    /// <summary>The step length every step gets (<c>1f / Hz</c>).</summary>
    public float Dt { get; }

    /// <summary>Longest frame time (seconds) one <see cref="Advance"/> counts.</summary>
    public double MaxFrameDelta { get; }
    /// <summary>Most steps one frame may produce.</summary>
    public int MaxStepsPerFrame { get; }
    /// <summary>Policy for time beyond <see cref="MaxStepsPerFrame"/>.</summary>
    public OverrunPolicy Overrun { get; }

    /// <summary>Time not yet stepped (seconds).</summary>
    public double Accumulator => _accumulator;

    /// <summary>Steps produced since construction.</summary>
    public long TotalSteps { get; private set; }

    /// <summary>
    /// Adds one frame's time and returns how many steps to run for it.
    /// </summary>
    /// <param name="frameSeconds">Real time since the previous frame; negative values count as 0.</param>
    /// <param name="clampedSeconds">The frame time after the <see cref="MaxFrameDelta"/> clamp (the time the frame represents).</param>
    /// <param name="overrun">True when the frame hit <see cref="MaxStepsPerFrame"/>.</param>
    public int Advance(double frameSeconds, out double clampedSeconds, out bool overrun)
    {
        clampedSeconds = Math.Clamp(frameSeconds, 0, MaxFrameDelta);
        var add = clampedSeconds;
        if (Overrun == OverrunPolicy.SlowMotion)
            add = Math.Min(add, _dt * MaxStepsPerFrame);

        _accumulator += add;
        var steps = 0;
        while (_accumulator >= _dt && steps < MaxStepsPerFrame)
        {
            _accumulator -= _dt;
            steps++;
        }

        overrun = steps == MaxStepsPerFrame;
        if (overrun)
        {
            switch (Overrun)
            {
                case OverrunPolicy.Drop:
                    _accumulator = 0;
                    break;
                case OverrunPolicy.Carry:
                    _accumulator = Math.Min(_accumulator, MaxFrameDelta);
                    break;
                case OverrunPolicy.SlowMotion:
                    if (_accumulator >= _dt)
                        _accumulator %= _dt;
                    break;
            }
        }

        TotalSteps += steps;
        return steps;
    }

    /// <summary>Forgets the time not yet stepped.</summary>
    public void Reset() => _accumulator = 0;

    /// <summary>Parses the <c>overrun</c> config value: <c>drop</c>, <c>carry</c> or <c>slow-motion</c> (case-insensitive).</summary>
    public static OverrunPolicy ParseOverrun(string? value)
    {
        var v = (value ?? "").Trim().Replace("-", "").Replace("_", "");
        if (v.Length == 0 || v.Equals("drop", StringComparison.OrdinalIgnoreCase))
            return OverrunPolicy.Drop;
        if (v.Equals("carry", StringComparison.OrdinalIgnoreCase))
            return OverrunPolicy.Carry;
        if (v.Equals("slowmotion", StringComparison.OrdinalIgnoreCase))
            return OverrunPolicy.SlowMotion;
        throw new ArgumentException($"Unknown fixed-step overrun policy '{value}'. Use drop, carry or slow-motion.", nameof(value));
    }
}
