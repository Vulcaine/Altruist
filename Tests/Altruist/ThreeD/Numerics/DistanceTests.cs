using System.Numerics;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.ThreeD.Numerics;

public class DistanceTests
{
    private const float Eps = 1e-4f;

    [Fact]
    public void Between_FullThreeD()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(3, 4, 12); // 3-4-12 = 13
        Distance3D.Between(a, b).Should().BeApproximately(13f, Eps);
    }

    [Fact]
    public void Between_Position3DOverloadMatchesVector3()
    {
        var a = new Vector3(1, 2, 3);
        var b = new Vector3(4, 6, 7);
        Distance3D.Between(Position3D.From(a), Position3D.From(b))
            .Should().BeApproximately(Distance3D.Between(a, b), Eps);
    }

    [Fact]
    public void Squared_FullThreeD() =>
        Distance3D.Squared(new Vector3(0, 0, 0), new Vector3(3, 4, 0)).Should().BeApproximately(25f, Eps);

    [Fact]
    public void Squared_Position3DOverloadMatchesVector3()
    {
        var a = new Vector3(1, 2, 3);
        var b = new Vector3(4, 6, 7);
        Distance3D.Squared(Position3D.From(a), Position3D.From(b))
            .Should().BeApproximately(Distance3D.Squared(a, b), Eps);
    }

    [Fact]
    public void Horizontal_IgnoresY()
    {
        var a = new Vector3(0, 0, 0);
        var b = new Vector3(3, 999, 4);
        Distance3D.Horizontal(a, b).Should().BeApproximately(5f, Eps);
    }

    [Fact]
    public void Horizontal_Position3DOverloadMatchesVector3()
    {
        var a = new Vector3(0, 50, 0);
        var b = new Vector3(3, -50, 4);
        Distance3D.Horizontal(Position3D.From(a), Position3D.From(b))
            .Should().BeApproximately(Distance3D.Horizontal(a, b), Eps);
    }

    [Fact]
    public void HorizontalSquared_IgnoresY()
    {
        var a = new Vector3(0, -50, 0);
        var b = new Vector3(3, 50, 4);
        Distance3D.HorizontalSquared(a, b).Should().BeApproximately(25f, Eps);
    }

    [Fact]
    public void HorizontalSquared_Position3DOverloadMatchesVector3()
    {
        var a = new Vector3(1, 0, 2);
        var b = new Vector3(4, 9, 6);
        Distance3D.HorizontalSquared(Position3D.From(a), Position3D.From(b))
            .Should().BeApproximately(Distance3D.HorizontalSquared(a, b), Eps);
    }
}
