/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Engine;
using Altruist.Gaming;

using Microsoft.Extensions.DependencyInjection;

using Moq;

namespace Tests.Gaming.Engine;

public class EngineFrameCounterTests
{
    [Fact]
    public void An_engine_advances_the_counter_it_was_given_once_per_frame()
    {
        var frames = new EngineFrameCounter();
        var driver = Manual(frames);

        driver.AdvanceFrames(3, 1.0 / 30);

        Assert.Equal(3, frames.Frame);
        Assert.Equal(3, driver.Engine.Frame);
    }

    [Fact]
    public void Two_engines_in_one_process_count_their_own_frames()
    {
        var first = new EngineFrameCounter();
        var second = new EngineFrameCounter();
        var firstDriver = Manual(first);
        var secondDriver = Manual(second);

        firstDriver.AdvanceFrames(100, 1.0 / 30);
        secondDriver.AdvanceFrames(2, 1.0 / 30);

        Assert.Equal(100, first.Frame);
        Assert.Equal(2, second.Frame);
    }

    private static EngineTestDriver Manual(EngineFrameCounter frames)
    {
        var status = new Mock<IServerStatus>();
        status.SetupGet(s => s.Status).Returns(ReadyState.Alive);
        var clock = new ManualEngineClock();
        var engine = new AltruistEngine(status.Object, new ServiceCollection().BuildServiceProvider(),
            new Mock<IGameWorldOrganizer>().Object, framerateHz: 30, worldStep: "inline", clock: clock, frames: frames);
        return new EngineTestDriver(engine, clock);
    }
}
