using System.Numerics;
using Altruist.Gaming.TwoD;
using Altruist.TwoD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.TwoD.Numerics;

public class Position2DTests
{
    [Fact]
    public void Keeps_sub_unit_coordinates()
    {
        var p = Position2D.From(new Vector2(1.75f, -2.5f));

        p.X.Should().Be(1.75f);
        p.Y.Should().Be(-2.5f);
        p.ToVector2().Should().Be(new Vector2(1.75f, -2.5f));
    }

    [Fact]
    public void Spatial_grid_queries_see_fractional_positions()
    {
        var grid = new SpatialGridIndex2D(cellSize: 10);
        var obj = new AnonymousWorldObject2D(
            new Transform2D(Position2D.Of(2.5f, -0.5f), Size2D.One, Scale2D.One, Rotation2D.Zero), archetype: "crate");
        grid.Add(obj);

        grid.Query("crate", 3, 0, radius: 0.75f, zoneId: "").Should().ContainSingle().Which.Should().BeSameAs(obj);
        grid.Query("crate", 0, 0, radius: 2f, zoneId: "").Should().BeEmpty();
    }
}
