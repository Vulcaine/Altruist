using Altruist.Gaming;

namespace Tests.Gaming.LagCompensation;

public sealed class LagCompensationServiceTests
{
    [Fact]
    public void RewindWorld_ReturnsCallbackResultAndRestoresState()
    {
        var lag = new LagCompensationService();
        bool wasRewound = false;

        bool accepted = lag.RewindWorld(0, () =>
        {
            wasRewound = lag.IsRewound;
            return true;
        });

        Assert.True(accepted);
        Assert.True(wasRewound);
        Assert.False(lag.IsRewound);
    }

    [Fact]
    public void RewindWorld_RestoresStateWhenCallbackThrows()
    {
        var lag = new LagCompensationService();

        Assert.Throws<InvalidOperationException>(() =>
            lag.RewindWorld<int>(0, () => throw new InvalidOperationException("boom")));
        Assert.False(lag.IsRewound);
    }

    [Fact]
    public void RewindOrRun_RunsDirectlyWhenServiceIsNull()
    {
        ILagCompensationService? lag = null;

        int value = lag.RewindOrRun(0, () => 42);

        Assert.Equal(42, value);
    }

    [Fact]
    public void RewindOrRun_UsesRewindWhenServiceExists()
    {
        var lag = new LagCompensationService();

        bool wasRewound = lag.RewindOrRun(0, () => lag.IsRewound);

        Assert.True(wasRewound);
        Assert.False(lag.IsRewound);
    }
}
