using System.Numerics;
using Altruist.Numerics;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using static Tests.Altruist.Numerics.BitExact;

namespace Tests.Altruist.TwoD.Numerics;

/// <summary>Math layer, contact / frame helpers: NormalFrame2D, Geometry2D.ClassifyBoxSide,
/// Aabb2D.Grow, Distance2D.Weighted, VectorMath2D.RotateDegrees, Scalar.ClampMinMax,
/// Angle.FullTurnRate / Sector / SectorToward, Rotation2D.AngleAligningForward — bit for bit against
/// the inline expressions they replace (copied from hand-written game code).</summary>
public class MathLayer2DFrameTests
{
    private static IEnumerable<(Vector2 V, Vector2 N, float K)> Cases(int count = 1500)
    {
        var vs = Vectors(count).ToArray();
        var us = Units(count).ToArray();
        for (var i = 0; i < Math.Min(vs.Length, us.Length); i++) yield return (vs[i], us[i], (i % 11) * 0.29f - 1.3f);
        // Normals with zero components, signed zeros.
        foreach (var n in new[] { new Vector2(0, 1), new Vector2(-0f, 1), new Vector2(1, -0f), new Vector2(0, -1), new Vector2(-1, 0) })
            foreach (var v in new[] { Vector2.Zero, new Vector2(-0f, -0f), new Vector2(3, -0f), new Vector2(-0f, 2), new Vector2(-4, 5) })
                yield return (v, n, 0.5f);
    }

    /// <summary>Same bits, or both zero (the only difference between Vector2.Dot and the scalar form).</summary>
    private static void SameUpToZeroSign(float actual, float expected)
    {
        if (expected == 0) actual.Should().Be(0);
        else Same(actual, expected);
    }

    [Fact]
    public void NormalFrame_left_matches_the_vector_contact_forms()
    {
        foreach (var (v, n, k) in Cases())
        {
            var f = NormalFrame2D.Left(n);
            // Inline: var t = new Vector2(-n.Y, n.X); Vector2.Dot(v, t); n * outN + t * outT.
            var t = new Vector2(-n.Y, n.X);
            Same(f.Normal, n);
            Same(f.Tangent, t);
            Same(f.Tangent, Direction2D.Perpendicular(n));
            Same(f.Along(v), Vector2.Dot(v, n));
            Same(f.Across(v), Vector2.Dot(v, t));
            Same(f.Into(v), -Vector2.Dot(v, n));
            var outN = v.X * 0.7f + k;
            var outT = v.Y * -0.3f - k;
            Same(f.Compose(outN, outT), n * outN + t * outT);
            Same(f.Compose(outN, outT), new Vector2(n.X * outN + t.X * outT, n.Y * outN + t.Y * outT));
            new NormalFrame2D(n, TangentSide2D.Left).Tangent.Should().Be(t);
        }
    }

    [Fact]
    public void NormalFrame_right_matches_the_surface_tangent_forms()
    {
        foreach (var (v, n, k) in Cases())
        {
            float nx = n.X, ny = n.Y;
            var f = NormalFrame2D.Right(n);
            // Inline: var tangent = new Vector2(n.Y, -n.X).
            Same(f.Tangent, new Vector2(ny, -nx));
            Same(f.Tangent, Direction2D.PerpendicularClockwise(n));
            new NormalFrame2D(n, TangentSide2D.Right).Tangent.Should().Be(f.Tangent);

            // var t = new Vector2(v.GroundNy, -v.GroundNx); var along = Vector2.Dot(vel, t);
            Same(f.Across(v), Vector2.Dot(v, new Vector2(ny, -nx)));
            // PreStepSpeed = MathF.Abs(vel.X * v.GroundNy - vel.Y * v.GroundNx)
            Same(MathF.Abs(f.Across(v)), MathF.Abs(v.X * ny - v.Y * nx));
            // closing = -(fall.X * b.Nx + fall.Y * b.Ny); compared with 0 / used only when > 0.
            var closing = -(v.X * nx + v.Y * ny);
            (f.Into(v) <= 0).Should().Be(closing <= 0);
            SameUpToZeroSign(f.Into(v), closing);
            // tilt = nose.X * nx + nose.Y * ny (compared through Abs and with 0).
            SameUpToZeroSign(f.Along(v), v.X * nx + v.Y * ny);
            // aimedIn = -(DashDirX * n.X + DashDirY * n.Y) >= min
            (f.Into(v) >= k).Should().Be(-(v.X * nx + v.Y * ny) >= k);

            // drop = (c.X - b.X) * nx + (c.Y - b.Y) * ny
            var p = v * 0.37f + new Vector2(k, -k);
            Same(f.HeightOf(p, v), (p.X - v.X) * nx + (p.Y - v.Y) * ny);
            Same(MathF.Max(0, f.HeightOf(p, v) - 0.6f - 0.02f), MathF.Max(0, (p.X - v.X) * nx + (p.Y - v.Y) * ny - 0.6f - 0.02f));
        }
    }

    [Fact]
    public void TangentToward_matches_the_sign_pick_forms()
    {
        foreach (var (v, n, _) in Cases())
        {
            float nx = n.X, ny = n.Y;
            var f = NormalFrame2D.Right(n);
            foreach (var facing in new[] { 1, -1 })
            {
                // float tx = ny, ty = -nx; var side = MathF.Sign(nose.X * tx + nose.Y * ty); if (side == 0) side = facing; tx *= side; ty *= side;
                float tx = ny, ty = -nx;
                var side = MathF.Sign(v.X * tx + v.Y * ty);
                if (side == 0) side = facing;
                tx *= side;
                ty *= side;
                Same(f.TangentToward(v, facing), new Vector2(tx, ty));

                // var s = ny * side >= 0 ? 1 : -1; dir = new Vector2(ny * s, -nx * s);
                var screenSide = facing;
                var s = ny * screenSide >= 0 ? 1 : -1;
                Same(f.TangentToward(new Vector2(screenSide, 0), 1), new Vector2(ny * s, -nx * s));
            }
        }
        NormalFrame2D.Right(Vector2.UnitY).TangentToward(Vector2.UnitY, -1).Should().Be(-Vector2.UnitX); // square: fallback
        NormalFrame2D.Right(Vector2.UnitY).TangentToward(new Vector2(-2, 5), 1).Should().Be(-Vector2.UnitX);
    }

    /// <summary>Copied from a game's hit-zone classifier.</summary>
    private static int InlineClassify(float localX, float localY, float halfW, float halfH, int facing, float axisBias)
    {
        var nx = localX * facing / halfW;
        var ny = localY / halfH;
        if (MathF.Abs(nx) * axisBias >= MathF.Abs(ny)) return nx >= 0 ? 0 : 1;
        return ny >= 0 ? 2 : 3;
    }

    [Fact]
    public void ClassifyBoxSide_reproduces_a_facing_hit_zone_classifier()
    {
        var map = new[] { BoxSide2D.Front, BoxSide2D.Back, BoxSide2D.Top, BoxSide2D.Bottom };
        var biases = new[] { 1f, 1.35f, 0.7f };
        var i = 0;
        foreach (var p in Vectors(3000, 3f))
        {
            var bias = biases[i++ % biases.Length];
            foreach (var facing in new[] { 1, -1 })
                Geometry2D.ClassifyBoxSide(new Vector2(p.X * facing, p.Y), 1.1f, 0.42f, bias)
                    .Should().Be(map[InlineClassify(p.X, p.Y, 1.1f, 0.42f, facing, bias)]);
        }
        // Corners and zeros: ties go front / top.
        Geometry2D.ClassifyBoxSide(new Vector2(1.1f, 0.42f), 1.1f, 0.42f, 1).Should().Be(BoxSide2D.Front);
        Geometry2D.ClassifyBoxSide(new Vector2(-1.1f, 0.42f), 1.1f, 0.42f, 0.99f).Should().Be(BoxSide2D.Top);
        Geometry2D.ClassifyBoxSide(Vector2.Zero, 1, 1, 1).Should().Be(BoxSide2D.Front);
        Geometry2D.ClassifyBoxSide(new Vector2(-0f, -1), 1, 1, 1).Should().Be(BoxSide2D.Bottom);
    }

    [Fact]
    public void Aabb_Grow_gives_the_inline_margin_bounds()
    {
        var box = new Aabb2D(52.5f, 60f, 0f, 9.25f);
        foreach (var p in Vectors(3000, 70f))
        {
            float x = p.X, y = p.Y;
            box.Grow(1, -0.3f).Contains(x, y).Should().Be(x >= box.MinX - 1 && x <= box.MaxX + 1 && y >= box.MinY + 0.3f && y <= box.MaxY - 0.3f);
            box.Grow(1.5f, 0.5f).Contains(x, y).Should().Be(box.Contains(x, y, 1.5f, 0.5f));
        }
        var g = box.Grow(1, -0.3f);
        Same(g.MinY, box.MinY + 0.3f);
        Same(g.MaxY, box.MaxY - 0.3f);
        Same(g.MinX, box.MinX - 1);
    }

    [Fact]
    public void Weighted_distance_matches_inline()
    {
        var vs = Vectors(2000).ToArray();
        for (var i = 1; i < vs.Length; i++)
        {
            Vector2 op = vs[i], ball = vs[i - 1];
            var inline = MathF.Abs(op.X - ball.X) + MathF.Abs(op.Y - ball.Y) * 0.5f;
            Same(Distance2D.WeightedBetween(op, ball, 0.5f), inline);
            Same(Distance2D.Weighted(op.X - ball.X, op.Y - ball.Y, 0.5f), inline);
        }
    }

    [Fact]
    public void RotateDegrees_matches_the_inline_stick_rotation()
    {
        var rots = Floats(200, 45f).ToArray();
        var vs = Vectors(200, 1f).ToArray();
        foreach (var rot in rots)
        {
            var c = DeterministicMath.Cos(rot * MathF.PI / 180);
            var s = DeterministicMath.Sin(rot * MathF.PI / 180);
            foreach (var v in vs)
            {
                var x = v.X * c - v.Y * s;
                var y = v.X * s + v.Y * c;
                Same(VectorMath2D.RotateDegrees(v, rot), new Vector2(x, y));
            }
        }
    }

    [Fact]
    public void ClampMinMax_matches_min_of_max()
    {
        foreach (var v in Floats(3000, 30f))
        {
            Same(Scalar.ClampMinMax(v, 0, 6.5f), MathF.Min(MathF.Max(v, 0), 6.5f));
            Same(Scalar.ClampMinMax(v, -2, -3), MathF.Min(MathF.Max(v, -2), -3)); // min > max: max wins
        }
        Same(Scalar.ClampMinMax(-0f, 0, 5), 0f); // +0, where Clamp keeps -0
        Same(Scalar.Clamp(-0f, 0, 5), -0f);
        float.IsNaN(Scalar.ClampMinMax(float.NaN, 0, 1)).Should().BeTrue();
    }

    [Fact]
    public void FullTurnRate_matches_inline()
    {
        foreach (var time in new[] { 0.42f, 0.5f, 1f / 3, 0.0001f, 2.5f })
            foreach (var turn in new[] { 1, -1, 2 })
                Same(Angle.FullTurnRate(time, turn), turn * MathF.PI * 2 / time);
    }

    [Fact]
    public void Sectors_match_the_inline_double_forms()
    {
        var r = new Random(9);
        var angles = new List<double> { 0, -0.0, Math.PI, -Math.PI, Math.PI / 2, -Math.PI / 2, 2 * Math.PI, 1e300, -7.5, 100 };
        for (var i = 0; i < 4000; i++) angles.Add((r.NextDouble() * 2 - 1) * 40);
        foreach (var a in angles)
        {
            Angle.Sector(a, 8).Should().Be((int)Math.Floor((DeterministicMath.Atan2(DeterministicMath.Sin(a), DeterministicMath.Cos(a)) + Math.PI) / (2 * Math.PI) * 8) % 8);
            Angle.Sector(a, 12).Should().Be((int)Math.Floor((DeterministicMath.Atan2(DeterministicMath.Sin(a), DeterministicMath.Cos(a)) + Math.PI) / (2 * Math.PI) * 12) % 12);
        }
        var ds = new List<(double, double)> { (0, 0), (-0.0, 0), (-1, 0), (-1, -0.0), (1, 0), (0, 1), (0, -1) };
        for (var i = 0; i < 4000; i++) ds.Add(((r.NextDouble() * 2 - 1) * 60, (r.NextDouble() * 2 - 1) * 30));
        foreach (var (dx, dy) in ds)
            foreach (var n in new[] { 8, 12, 16 })
                Angle.SectorToward(dx, dy, n).Should().Be((int)Math.Floor((DeterministicMath.Atan2(dy, dx) + Math.PI) / (2 * Math.PI) * n) % n);
        Angle.SectorToward(-1, 0, 8).Should().Be(0); // +π wraps to slice 0
        Angle.SectorToward(-1, -1e-9, 8).Should().Be(0);
        Angle.SectorToward(1, -1e-9, 8).Should().Be(3);
        Angle.SectorToward(1, 0, 8).Should().Be(4);
    }

    [Fact]
    public void AngleAligningForward_matches_the_aim_form()
    {
        foreach (var v in Vectors(3000, 1f))
            foreach (var facing in new[] { 1, -1 })
            {
                var noseLocal = facing > 0 ? 0 : MathF.PI;
                Same(Rotation2D.AngleAligningForward(v, facing), DeterministicMath.Atan2(v.Y, v.X) - noseLocal);
                // Pointing the facing's local axis along the direction.
                if (v.Length() > 0.1f)
                {
                    var forward = Rotation2D.FromRadians(Rotation2D.AngleAligningForward(v, facing)).Rotate(new Vector2(facing, 0));
                    var d = Vector2.Normalize(v);
                    forward.X.Should().BeApproximately(d.X, 1e-5f);
                    forward.Y.Should().BeApproximately(d.Y, 1e-5f);
                }
            }
    }
}
