using System.Numerics;
using Altruist.ThreeD.Numerics;
using FluentAssertions;

namespace Tests.Altruist.ThreeD.Numerics;

public class Transform3DTests
{
    [Fact]
    public void Identity_sits_at_the_origin()
    {
        Transform3D.Identity.Position.ToVector3().Should().Be(Vector3.Zero);
        Transform3D.Identity.Size.ToVector3().Should().Be(Vector3.One);
        Transform3D.Identity.Scale.ToVector3().Should().Be(Vector3.One);
        Transform3D.Identity.Rotation.Value.Should().Be(Quaternion.Identity);
    }

    [Fact]
    public void ToString_prints_the_part_values_not_type_names()
    {
        var t = new Transform3D(Position3D.Of(0, 1, 5), Size3D.Of(2, 3, 4), Scale3D.Uniform(1), Rotation3D.Identity);

        var text = t.ToString();

        text.Should().NotContain(nameof(Position3D)).And.NotContain(nameof(Rotation3D));
        text.Should().Contain(new Vector3(0, 1, 5).ToString());
        text.Should().Contain(new Vector3(2, 3, 4).ToString());
        text.Should().Contain(Quaternion.Identity.ToString());
    }
}
