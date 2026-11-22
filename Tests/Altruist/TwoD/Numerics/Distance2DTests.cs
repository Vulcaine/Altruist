using System.Numerics;
using Altruist.TwoD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.TwoD.Numerics;

public class Distance2DTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void Between_PythagoreanCase()
    {
        Distance2D.Between(new Vector2(0, 0), new Vector2(3, 4))
            .Should().BeApproximately(5f, Eps);
    }

    [Fact]
    public void Between_CoincidentIsZero()
    {
        Distance2D.Between(new Vector2(7, 9), new Vector2(7, 9))
            .Should().BeApproximately(0f, Eps);
    }

    [Fact]
    public void Squared_AvoidsSqrt()
    {
        Distance2D.Squared(new Vector2(0, 0), new Vector2(3, 4))
            .Should().BeApproximately(25f, Eps);
    }

    [Fact]
    public void Between_Position2DOverloadMatchesVector2()
    {
        var a = new Vector2(0, 0);
        var b = new Vector2(3, 4);
        Distance2D.Between(Position2D.Of(0, 0), Position2D.Of(3, 4))
            .Should().BeApproximately(Distance2D.Between(a, b), Eps);
    }

    [Fact]
    public void Squared_Position2DOverloadMatchesVector2()
    {
        Distance2D.Squared(Position2D.Of(0, 0), Position2D.Of(3, 4))
            .Should().BeApproximately(25f, Eps);
    }
}
