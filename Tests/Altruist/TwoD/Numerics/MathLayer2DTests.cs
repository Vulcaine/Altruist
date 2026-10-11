using System.Numerics;
using Altruist.Numerics;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using static Tests.Altruist.Numerics.BitExact;

namespace Tests.Altruist.TwoD.Numerics;

/// <summary>Math layer, 2D: VectorMath2D, Direction2D additions, Rotation2D.AngleAligningUp,
/// Geometry2D, Aabb2D, Polyline2D — bit for bit against the inline expressions they replace.</summary>
public class MathLayer2DTests
{
    private const float Deg = MathF.PI / 180f;

    [Fact]
    public void Lengths_match_inline_and_Vector2_Length_bits()
    {
        foreach (var v in Vectors(5000, 1000f))
        {
            var inline = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
            Same(VectorMath2D.Length(v), inline);
            Same(VectorMath2D.Length(v.X, v.Y), inline);
            Same(v.Length(), inline); // the runtime's Vector2.Length gives the same bits
        }
    }

    [Fact]
    public void NormalizeOrZero_matches_divide_by_length_or_one()
    {
        foreach (var v in Vectors())
        {
            var l = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
            var len = l;
            if (l == 0) l = 1;
            var expected = new Vector2(v.X / l, v.Y / l);
            Same(VectorMath2D.NormalizeOrZero(v), expected);
            Same(VectorMath2D.NormalizeOrZero(v, out var got), expected);
            Same(got, len);
        }
        VectorMath2D.NormalizeOrZero(Vector2.Zero).Should().Be(Vector2.Zero);
        VectorMath2D.NormalizeOrZero(new Vector2(1e-10f, 0)).Should().Be(Vector2.UnitX); // no epsilon
        VectorMath2D.NormalizeOr(Vector2.Zero, Vector2.UnitY).Should().Be(Vector2.UnitY);
        VectorMath2D.NormalizeOr(new Vector2(0, -3), Vector2.UnitY).Should().Be(-Vector2.UnitY);
    }

    [Fact]
    public void Component_helpers_match_inline_bits()
    {
        var vs = Vectors(1500).ToArray();
        var us = Units(1500).ToArray();
        for (var i = 0; i < Math.Min(vs.Length, us.Length); i++)
        {
            var v = vs[i];
            var n = us[i];
            float vx = v.X, vy = v.Y, nx = n.X, ny = n.Y;
            var k = (i % 7) * 0.37f - 1f;

            Same(VectorMath2D.Cross(v, n), vx * ny - vy * nx);
            Same(VectorMath2D.Cross(k, v), new Vector2(-k * vy, k * vx));
            Same(VectorMath2D.Project(v, n), n * Vector2.Dot(v, n));
            Same(VectorMath2D.Reject(v, n), v - n * Vector2.Dot(v, n));
            Same(VectorMath2D.AddAlong(v, n, k), new Vector2(vx + nx * k, vy + ny * k));
            Same(VectorMath2D.AddAlong(v, n, k), v + n * k);

            // Set / approach the component along a direction (surface tangent drive).
            var into = vx * nx + vy * ny;
            Same(VectorMath2D.WithComponentAlong(v, n, 3f), new Vector2(vx + nx * (3f - into), vy + ny * (3f - into)));
            var vt = vx * nx + vy * ny;
            var next = vt < 20f ? MathF.Min(vt + 0.4f, 20f) : MathF.Max(vt - 0.4f, 20f);
            Same(VectorMath2D.ApproachComponentAlong(v, n, 20f, 0.4f), new Vector2(vx + nx * (next - vt), vy + ny * (next - vt)));

            // Redirect all speed along the axis.
            var speed = MathF.Sqrt(vx * vx + vy * vy);
            var s2 = vx * nx + vy * ny >= 0 ? speed : -speed;
            Same(VectorMath2D.RedirectAlong(v, n), new Vector2(nx * s2, ny * s2));

            // Clamp length.
            var s = v.Length();
            Same(VectorMath2D.ClampLength(v, 20f), s > 20f ? v / s * 20f : v);

            // Shorten by an amount.
            if (speed > 0) Same(VectorMath2D.ShortenBy(v, speed, 0.7f), new Vector2(vx - vx / speed * 0.7f, vy - vy / speed * 0.7f));

            // Lerp.
            Same(VectorMath2D.Lerp(v, n, k), v + (n - v) * k);
        }
    }

    [Fact]
    public void Component_helpers_semantics()
    {
        var v = new Vector2(3, -4);
        VectorMath2D.WithComponentAlong(v, Vector2.UnitY, 2).Should().Be(new Vector2(3, 2));
        VectorMath2D.ApproachComponentAlong(v, Vector2.UnitX, 10, 1).Should().Be(new Vector2(4, -4));
        VectorMath2D.RedirectAlong(v, -Vector2.UnitX).Should().Be(new Vector2(5, -0f));
        VectorMath2D.ClampLength(v, 2.5f).Length().Should().BeApproximately(2.5f, 1e-5f);
        VectorMath2D.ClampLength(v, 10).Should().Be(v);
        VectorMath2D.Cross(Vector2.UnitX, Vector2.UnitY).Should().Be(1);
    }

    [Fact]
    public void Direction_additions()
    {
        Direction2D.PerpendicularClockwise(Vector2.UnitY).Should().Be(Vector2.UnitX);
        Direction2D.Perpendicular(Direction2D.PerpendicularClockwise(new Vector2(2, 5))).Should().Be(new Vector2(2, 5));
        foreach (var d in Floats(500, 720f))
        {
            Same(Direction2D.FromPolarDegrees(d), new Vector2(DeterministicMath.Cos(d * Deg), DeterministicMath.Sin(d * Deg)));
            Same(Direction2D.FromPolar(d * Deg), new Vector2(DeterministicMath.Cos(d * Deg), DeterministicMath.Sin(d * Deg)));
        }
        Direction2D.FromPolarDegrees(90).X.Should().BeApproximately(0, 1e-6f);
        Direction2D.FromPolarDegrees(90).Y.Should().BeApproximately(1, 1e-6f);
    }

    [Fact]
    public void ClampElevation_matches_inline_bits()
    {
        foreach (var u in Units(2000))
        {
            float ux = u.X, uy = u.Y;
            if (uy > 0.92f)
            {
                uy = 0.92f;
                ux = MathF.Sign(ux) * MathF.Sqrt(1 - uy * uy);
            }
            else if (uy < -0.3f)
            {
                uy = -0.3f;
                ux = MathF.Sign(ux) * MathF.Sqrt(1 - uy * uy);
            }
            Same(Direction2D.ClampElevation(u, -0.3f, 0.92f), new Vector2(ux, uy));
        }
        Direction2D.ClampElevation(Vector2.UnitY, -0.3f, 0.92f).Should().Be(new Vector2(0, 0.92f));
    }

    [Fact]
    public void AngleAligningUp_rotates_local_up_onto_the_normal()
    {
        foreach (var n in Units(300))
        {
            Same(Rotation2D.AngleAligningUp(n), DeterministicMath.Atan2(-n.X, n.Y));
            var up = Rotation2D.FromRadians(Rotation2D.AngleAligningUp(n)).Rotate(Vector2.UnitY);
            up.X.Should().BeApproximately(n.X, 1e-5f);
            up.Y.Should().BeApproximately(n.Y, 1e-5f);
        }
        Rotation2D.AngleAligningUp(Vector2.UnitY).Should().Be(0);
    }

    [Fact]
    public void Geometry_matches_inline_bits()
    {
        var vs = Vectors(2000, 4f).ToArray();
        var us = Units(2000).ToArray();
        for (var i = 0; i < Math.Min(vs.Length, us.Length); i++)
        {
            var local = vs[i];
            const float hw = 1.1f, hh = 0.45f;
            Geometry2D.BoxContainsLocal(local, hw + 0.3f, hh + 0.4f)
                .Should().Be(!(MathF.Abs(local.X) > hw + 0.3f || MathF.Abs(local.Y) > hh + 0.4f));
            var dx = MathF.Max(0, MathF.Abs(local.X) - hw);
            var dy = MathF.Max(0, MathF.Abs(local.Y) - hh);
            Same(Geometry2D.BoxDistanceSquaredLocal(local, hw, hh), dx * dx + dy * dy);

            var rel = local.X;
            Same(Geometry2D.RotatedBoxHalfExtentY(hw, hh, rel), hw * MathF.Abs(DeterministicMath.Sin(rel)) + hh * MathF.Abs(DeterministicMath.Cos(rel)));

            var p = local * 3;
            var d = us[i];
            var o = vs[(i + 1) % vs.Length];
            var (along, offset) = Geometry2D.PointToRay(o, d, p);
            Same(along, (p.X - o.X) * d.X + (p.Y - o.Y) * d.Y);
            Same(offset, MathF.Abs((p.X - o.X) * d.Y - (p.Y - o.Y) * d.X));
        }
    }

    [Fact]
    public void Geometry_semantics()
    {
        Geometry2D.BoxContainsLocal(new Vector2(1, 0.5f), 1, 0.5f).Should().BeTrue();
        Geometry2D.BoxContainsLocal(new Vector2(1.01f, 0), 1, 0.5f).Should().BeFalse();
        Geometry2D.BoxDistanceSquaredLocal(new Vector2(4, 4.5f), 1, 0.5f).Should().Be(25);
        Geometry2D.BoxDistanceSquaredLocal(Vector2.Zero, 1, 0.5f).Should().Be(0);
        Geometry2D.RotatedBoxHalfExtentY(2, 1, 0).Should().Be(1);
        Geometry2D.RotatedBoxHalfExtentY(2, 1, MathF.PI / 2).Should().BeApproximately(2, 1e-6f);
        Geometry2D.RotatedBoxHalfExtentX(2, 1, 0).Should().Be(2);
        Geometry2D.PointToRay(Vector2.Zero, Vector2.UnitX, new Vector2(-3, -2)).Should().Be((-3f, 2f));
    }

    [Fact]
    public void Aabb_contains_center_and_margins()
    {
        var box = new Aabb2D(-2, 4, 1, 3);
        box.Contains(-2, 1).Should().BeTrue();
        box.Contains(4.01f, 2).Should().BeFalse();
        box.Contains(4.01f, 2, 0.5f, 0).Should().BeTrue();
        box.Contains(0, 0.9f, 0.5f, 0).Should().BeFalse();
        box.Contains(new Vector2(0, 2)).Should().BeTrue();
        box.Center.Should().Be(new Vector2(1, 2));
        box.Width.Should().Be(6);
        box.Height.Should().Be(2);
        Aabb2D.FromCorners(new Vector2(4, 3), new Vector2(-2, 1)).Should().Be(box);
        box.Intersects(new Aabb2D(4, 5, 3, 9)).Should().BeTrue();
        box.Intersects(new Aabb2D(4.1f, 5, 0, 9)).Should().BeFalse();
        foreach (var v in Vectors(500, 6f))
        {
            box.Contains(v.X, v.Y, 1.3f, 0).Should().Be(v.X >= box.MinX - 1.3f && v.X <= box.MaxX + 1.3f && v.Y >= box.MinY && v.Y <= box.MaxY);
            Same(box.Center, new Vector2((box.MinX + box.MaxX) / 2, (box.MinY + box.MaxY) / 2));
        }
    }

    private static List<Vector2> InlineArc(float cx, float cy, float r, float a0, float a1)
    {
        var pts = new List<Vector2>();
        var steps = Math.Max(2, (int)MathF.Ceiling(MathF.Abs(a1 - a0) / 7.5f));
        for (var i = 0; i <= steps; i++)
        {
            var a = (a0 + (a1 - a0) * i / steps) * Deg;
            pts.Add(new Vector2(cx + r * DeterministicMath.Cos(a), cy + r * DeterministicMath.Sin(a)));
        }
        return pts;
    }

    private static List<Vector2> InlineDedupe(List<Vector2> pts)
    {
        var outPts = new List<Vector2>();
        foreach (var p in pts)
            if (outPts.Count == 0 || Vector2.DistanceSquared(outPts[^1], p) > 1e-10f) outPts.Add(p);
        if (outPts.Count > 1 && Vector2.DistanceSquared(outPts[0], outPts[^1]) < 1e-10f) outPts.RemoveAt(outPts.Count - 1);
        return outPts;
    }

    [Theory]
    [InlineData(10f, 4f, 6f, 270f, 360f)]
    [InlineData(-30.5f, 2.25f, 12f, 180f, 90f)]
    [InlineData(0f, 0f, 1f, 0f, 3f)]
    [InlineData(1f, 2f, 3f, -45f, 400f)]
    public void Arc_matches_inline_tessellation_bits(float cx, float cy, float r, float a0, float a1)
    {
        var got = new List<Vector2>();
        Polyline2D.AppendArc(got, cx, cy, r, a0, a1, 7.5f);
        var expected = InlineArc(cx, cy, r, a0, a1);
        got.Should().HaveCount(expected.Count);
        for (var i = 0; i < got.Count; i++) Same(got[i], expected[i]);
    }

    [Fact]
    public void RemoveDuplicates_matches_inline_and_closes_loops()
    {
        var pts = new List<Vector2> { new(-40, 0) };
        pts.Add(new Vector2(-40, 0)); // duplicate
        Polyline2D.AppendArc(pts, -34, 6, 6, 180, 270, 7.5f);
        pts.Add(new Vector2(34, 0));
        Polyline2D.AppendArc(pts, 34, 6, 6, 270, 360, 7.5f);
        pts.Add(new Vector2(40, 30));
        pts.Add(new Vector2(-40, 30));
        pts.Add(new Vector2(-40, 0)); // closes onto the first
        var got = Polyline2D.RemoveDuplicates(pts);
        var expected = InlineDedupe(pts);
        got.Should().Equal(expected);
        got[^1].Should().Be(new Vector2(-40, 30));
        Polyline2D.RemoveDuplicates(pts, closed: false)[^1].Should().Be(new Vector2(-40, 0));
        Polyline2D.RemoveDuplicates(new List<Vector2>()).Should().BeEmpty();
        Polyline2D.RemoveDuplicates(new List<Vector2> { Vector2.One }).Should().Equal(Vector2.One);
    }
}
