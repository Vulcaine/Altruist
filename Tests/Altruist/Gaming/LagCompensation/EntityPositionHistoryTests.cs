using Altruist.Gaming;

namespace Tests.Gaming.LagCompensation;

public sealed class EntityPositionHistoryTests
{
    [Fact]
    public void GetInterpolated_ReturnsExactSnapshot_WhenTickExists()
    {
        var history = new EntityPositionHistory();
        history.Record(10, 1f, 2f, 3f, 0.25f);
        history.Record(11, 9f, 9f, 9f, 1f);

        var snapshot = history.GetInterpolated(10);

        Assert.NotNull(snapshot);
        Assert.Equal(10, snapshot.Value.Tick);
        Assert.Equal(1f, snapshot.Value.X);
        Assert.Equal(2f, snapshot.Value.Y);
        Assert.Equal(3f, snapshot.Value.Z);
        Assert.Equal(0.25f, snapshot.Value.Yaw);
    }

    [Fact]
    public void GetInterpolated_ReturnsMidpoint_WhenTickIsBracketed()
    {
        var history = new EntityPositionHistory();
        history.Record(10, 0f, 0f, 0f, 0f);
        history.Record(12, 10f, 20f, 30f, 2f);

        var snapshot = history.GetInterpolated(11);

        Assert.NotNull(snapshot);
        Assert.Equal(11, snapshot.Value.Tick);
        Assert.Equal(5f, snapshot.Value.X);
        Assert.Equal(10f, snapshot.Value.Y);
        Assert.Equal(15f, snapshot.Value.Z);
        Assert.Equal(1f, snapshot.Value.Yaw);
    }

    [Fact]
    public void GetInterpolated_FallsBackToNearest_WhenTickCannotBeBracketed()
    {
        var history = new EntityPositionHistory();
        history.Record(10, 1f, 0f, 0f, 0f);
        history.Record(12, 3f, 0f, 0f, 0f);

        var snapshot = history.GetInterpolated(20);

        Assert.NotNull(snapshot);
        Assert.Equal(12, snapshot.Value.Tick);
        Assert.Equal(3f, snapshot.Value.X);
    }

    [Fact]
    public void GetInterpolated_InterpolatesYawUsingShortestAngle()
    {
        var history = new EntityPositionHistory();
        history.Record(10, 0f, 0f, 0f, DegreesToRadians(179f));
        history.Record(12, 0f, 0f, 0f, DegreesToRadians(-179f));

        var snapshot = history.GetInterpolated(11);

        Assert.NotNull(snapshot);
        Assert.True(Math.Abs(Math.Abs(snapshot.Value.Yaw) - MathF.PI) < 0.001f);
    }

    private static float DegreesToRadians(float degrees) => degrees * MathF.PI / 180f;
}
