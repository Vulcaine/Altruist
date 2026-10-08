using System.Numerics;
using Altruist.Gaming.TwoD;
using Altruist.Numerics;
using Altruist.Physx;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using Tests.Altruist.Physx.Fakes;
using static Tests.Altruist.Numerics.BitExact;

namespace Tests.Altruist.Gaming.TwoD;

/// <summary>Gameplay layer: FacingFrame2D, the horizontal / vertical velocity verbs, FlipHalfTurn,
/// UpDirection, HeightAbove and PredictZoneEntry give the bits of the hand-written forms they
/// replace (spelled out in each test).</summary>
public class FacingFrame2DTests
{
    private const float HalfW = 1.1f;
    private const float HalfH = 0.42f;

    private static IEnumerable<(IPhysxBody2D Body, int Facing, Vector2 P)> Frames(int count = 700)
    {
        var ps = Vectors(count, 20f, seed: 41).ToArray();
        var qs = Vectors(count, 3f, seed: 42).ToArray();
        var angles = Floats(count, 7f, seed: 43).ToArray();
        for (var i = 0; i < count; i++)
        {
            var body = new FakeBody2D { Position = ps[i], RotationZ = angles[i % angles.Length], LinearVelocity = qs[(i + 1) % qs.Length] * 9 };
            yield return (body, i % 2 == 0 ? 1 : -1, ps[i] + qs[i]);
        }
    }

    private static int InlineClassify(float localX, float localY, float halfW, float halfH, int facing, float axisBias)
    {
        var nx = localX * facing / halfW;
        var ny = localY / halfH;
        if (MathF.Abs(nx) * axisBias >= MathF.Abs(ny)) return nx >= 0 ? 0 : 1;
        return ny >= 0 ? 2 : 3;
    }

    private static readonly BoxSide2D[] Zones = { BoxSide2D.Front, BoxSide2D.Back, BoxSide2D.Top, BoxSide2D.Bottom };

    [Fact]
    public void Nose_up_tip_and_front_match_inline()
    {
        foreach (var (body, facing, p) in Frames())
        {
            var f = new FacingFrame2D(body, facing, HalfW, HalfH);
            var nose = body.GetWorldVector(new Vector2(facing, 0));
            Same(f.Nose, nose);
            Same(f.Up, body.GetWorldVector(new Vector2(0, 1)));
            Same(f.NoseTip, body.Position + nose * HalfW);
            f.IsInFront(p).Should().Be(body.GetLocalPoint(p).X * facing > 0);
            var vel = body.LinearVelocity;
            Same(f.NoseAlignment(vel), nose.X * vel.X + nose.Y * vel.Y);
            // nose.X * facing < alignDot (alignment with the facing's horizontal).
            (f.NoseAlignment(new Vector2(facing, 0)) < 0.8f).Should().Be(nose.X * facing < 0.8f);
        }
    }

    [Fact]
    public void Zones_match_the_inline_classifier_for_points_and_directions()
    {
        foreach (var (body, facing, p) in Frames())
        {
            var f = new FacingFrame2D(body, facing, HalfW, HalfH);
            var local = body.GetLocalPoint(p);
            f.ZoneAt(p, 1.35f).Should().Be(Zones[InlineClassify(local.X, local.Y, HalfW, HalfH, facing, 1.35f)]);
            var n = Vector2.Normalize(p - body.Position + new Vector2(0.01f, 0));
            var ln = body.GetLocalVector(-n);
            f.ZoneToward(-n, 1).Should().Be(Zones[InlineClassify(ln.X, ln.Y, HalfW, HalfH, facing, 1)]);
        }
        // Upright, facing left: a point to the left is the front, below is the bottom.
        var b = new FakeBody2D();
        var left = new FacingFrame2D(b, -1, HalfW, HalfH);
        left.ZoneAt(new Vector2(-2, 0), 1).Should().Be(BoxSide2D.Front);
        left.ZoneAt(new Vector2(2, 0), 1).Should().Be(BoxSide2D.Back);
        left.ZoneAt(new Vector2(0, -1), 1).Should().Be(BoxSide2D.Bottom);
        left.ZoneToward(Vector2.UnitY, 1).Should().Be(BoxSide2D.Top);
    }

    [Fact]
    public void UndersideEnds_match_the_ray_start_loop()
    {
        foreach (var (body, facing, _) in Frames())
        {
            var f = new FacingFrame2D(body, facing, HalfW, HalfH);
            var up = body.GetWorldVector(new Vector2(0, 1));
            var nose = body.GetWorldVector(new Vector2(facing, 0));
            var c = body.Position;
            var starts = new List<Vector2>();
            foreach (var end in new[] { -1, 1 }) starts.Add(c + nose * (HalfW * end) - up * HalfH);
            var (back, front) = f.UndersideEnds();
            Same(back, starts[0]);
            Same(front, starts[1]);
        }
    }

    [Fact]
    public void AimRotationToward_matches_the_nose_aim_form()
    {
        foreach (var (body, facing, p) in Frames())
        {
            var f = new FacingFrame2D(body, facing, HalfW, HalfH);
            var noseLocal = facing > 0 ? 0 : MathF.PI;
            Same(f.AimRotationToward(p), MathF.Atan2(p.Y, p.X) - noseLocal);
        }
    }

    [Fact]
    public void Axis_velocity_verbs_write_components_directly()
    {
        foreach (var v in Vectors(2000, 40f))
        {
            var k = v.X * 0.01f + 0.3f;
            var flipDir = v.Y > 0 ? 1 : -1;

            // (dir * speed, vel.Y * 0.3f)
            var b = new FakeBody2D { LinearVelocity = v };
            b.SetVelocityX(flipDir * 31.5f);
            b.ScaleVelocityY(0.3f);
            Same(b.LinearVelocity, new Vector2(flipDir * 31.5f, v.Y * 0.3f));

            // (flipDir * tier, MathF.Min(MathF.Max(vel.Y, 0), hop))
            b = new FakeBody2D { LinearVelocity = v };
            b.SetVelocityX(flipDir * MathF.Max(0, v.X * flipDir));
            b.ClampVelocityY(0, 6.5f);
            Same(b.LinearVelocity, new Vector2(flipDir * MathF.Max(0, v.X * flipDir), MathF.Min(MathF.Max(v.Y, 0), 6.5f)));

            // (vel.X * (1 - cut * MathF.Pow(MathF.Min(1, dir.Y), curve)), vel.Y)
            var factor = 1 - 0.8f * MathF.Pow(MathF.Min(1, MathF.Abs(k)), 1.5f);
            b = new FakeBody2D { LinearVelocity = v };
            b.ScaleVelocityX(factor);
            Same(b.LinearVelocity, new Vector2(v.X * factor, v.Y));

            b = new FakeBody2D { LinearVelocity = v };
            b.SetVelocityY(k);
            Same(b.LinearVelocity, new Vector2(v.X, k));
        }
        var z = new FakeBody2D { LinearVelocity = new Vector2(1, -0f) };
        z.ClampVelocityY(0, 3);
        Same(z.LinearVelocity.Y, 0f); // +0, as MathF.Max(-0f, 0) gives
    }

    [Fact]
    public void FlipHalfTurn_up_direction_and_height_above()
    {
        foreach (var (body, facing, p) in Frames(300))
        {
            var pos = body.Position;
            var rot = body.RotationZ;
            var vel = body.LinearVelocity;
            Same(body.UpDirection(), body.GetWorldVector(new Vector2(0, 1)));
            Same(body.HeightAbove(p), body.GetLocalPoint(p).Y);
            body.FlipHalfTurn();
            Same(body.Position, pos);
            Same(body.RotationZ, rot + MathF.PI);
            Same(body.LinearVelocity, vel);
        }
        var platform = new FakeBody2D { Position = new Vector2(0, 5), RotationZ = MathF.PI / 2 };
        platform.HeightAbove(new Vector2(-2, 5)).Should().BeApproximately(2, 1e-5f); // its up is -X
    }

    [Fact]
    public void PredictZoneEntry_is_FirstZoneEntered_from_the_body()
    {
        var zones = new[] { new Aabb2D(52f, 58f, 0f, 8f).Grow(1, -0.3f), new Aabb2D(-58f, -52f, 0f, 8f).Grow(1.5f, 0.5f) };
        var ps = Vectors(500, 50f, seed: 51).ToArray();
        var vs = Vectors(500, 40f, seed: 52).ToArray();
        for (var i = 0; i < ps.Length; i++)
        {
            var b = new FakeBody2D { Position = ps[i], LinearVelocity = vs[i] };
            b.PredictZoneEntry(zones, 27, 1f / 30, 60, 1.2f).Should().Be(Ballistics2D.FirstZoneEntered(ps[i], vs[i], 27, 1f / 30, 60, zones, 1.2f));
        }
        var shot = new FakeBody2D { Position = new Vector2(40, 3), LinearVelocity = new Vector2(30, 4) };
        var (zone, step) = shot.PredictZoneEntry(zones, 27, 1f / 30, 60);
        zone.Should().Be(0);
        step.Should().BeGreaterThan(0);
    }
}
