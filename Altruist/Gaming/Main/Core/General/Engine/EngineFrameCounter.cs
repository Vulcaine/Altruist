/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Engine;

/// <summary>
/// The frame the engine of this application is running: what game code compares cooldowns, action windows and
/// sync intervals against. Injected rather than read from a static, so two engines in one process (a test host
/// running an in-process root next to a live server) each count their own frames.
/// </summary>
public interface IEngineFrameCounter
{
    /// <summary>Engine frames run so far (the frame being run, while inside one); 0 before the engine's first frame,
    /// and always 0 in an application without an engine.</summary>
    long Frame { get; }
}

/// <summary>The frame counter the engine advances at the start of each frame.</summary>
[Service(typeof(IEngineFrameCounter))]
[ConditionalOnConfig("altruist:game")]
public sealed class EngineFrameCounter : IEngineFrameCounter
{
    private long _frame;

    /// <inheritdoc/>
    public long Frame => Interlocked.Read(ref _frame);

    /// <summary>Starts the next frame and returns its number.</summary>
    internal long Advance() => Interlocked.Increment(ref _frame);
}
