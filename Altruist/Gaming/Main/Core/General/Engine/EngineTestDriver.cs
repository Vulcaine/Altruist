/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Engine;

/// <summary>Where the engine runs the world step (<c>altruist:game:engine:world-step</c>).</summary>
public enum WorldStepMode
{
    /// <summary>On a separate world-worker task, with the frame times summed until it picks them up (default).</summary>
    Worker,

    /// <summary>
    /// On the engine loop, as the last phase of each frame: next-tick queue, <c>[Cycle]</c> tasks,
    /// dynamic tasks, effects, then the world step, all on one thread in this order.
    /// </summary>
    Inline,
}

/// <summary>
/// Runs engine frames by hand, deterministically: the engine is never started (no thread, no timer),
/// time comes from a <see cref="ManualEngineClock"/>, and each frame runs the same pipeline as the
/// live loop (next-tick queue → cycles → dynamic tasks → effects → world step). Build the engine
/// with <c>world-step: inline</c> to have the world step inside the frame.
/// </summary>
public sealed class EngineTestDriver
{
    private readonly AltruistEngine _engine;

    public EngineTestDriver(AltruistEngine engine, ManualEngineClock clock)
    {
        _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        Clock = clock ?? throw new ArgumentNullException(nameof(clock));
        if (engine.Enabled)
            throw new InvalidOperationException("EngineTestDriver drives a stopped engine; do not Start it.");
    }

    public AltruistEngine Engine => _engine;
    public ManualEngineClock Clock { get; }

    /// <summary>Frames run by the engine so far.</summary>
    public long Frame => _engine.Frame;

    /// <summary>Advances the clock by <paramref name="frameSeconds"/> and runs one frame, <paramref name="frames"/> times.</summary>
    public void AdvanceFrames(int frames, double frameSeconds)
    {
        if (frames < 0)
            throw new ArgumentOutOfRangeException(nameof(frames));
        for (var i = 0; i < frames; i++)
        {
            Clock.Advance(frameSeconds);
            _engine.RunFrame(Clock.NowTicks, (float)frameSeconds);
        }
    }

    /// <summary>Runs whole frames of <paramref name="frameSeconds"/> covering <paramref name="seconds"/> (rounded to the nearest frame).</summary>
    public void AdvanceSeconds(double seconds, double frameSeconds = 1.0 / 120)
    {
        if (frameSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(frameSeconds));
        AdvanceFrames((int)Math.Round(seconds / frameSeconds), frameSeconds);
    }

    /// <summary>Queues <paramref name="action"/> for the next frame (= <c>WaitForNextTick</c>).</summary>
    public void Post(Action action) => _engine.WaitForNextTick(action);
}
