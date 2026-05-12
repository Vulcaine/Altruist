using System.Numerics;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.ThreeD.Numerics;

public class DistanceTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void Calculate_FullThreeD()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(3, 4, 12); // 3-4-12 = 13
        Distance.Calculate(a, b).Should().BeApproximately(13f, Eps);
    }

    [Fact]
    public void Squared_FullThreeD() =>
        Distance.Squared(new Vector3(0, 0, 0), new Vector3(3, 4, 0)).Should().BeApproximately(25f, Eps);

    [Fact]
    public void Horizontal_IgnoresY()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(3, 999, 4);
        Distance.Horizontal(a, b).Should().BeApproximately(5f, Eps);
    }

    [Fact]
    public void HorizontalSquared_IgnoresY()
    {
        var a = new Vector3(0, -50, 0);
        var b = new Vector3(3, 50, 4);
        Distance.HorizontalSquared(a, b).Should().BeApproximately(25f, Eps);
    }
}
