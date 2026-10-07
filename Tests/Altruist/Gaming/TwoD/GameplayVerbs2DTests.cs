using System.Numerics;
using Altruist.Gaming.TwoD;
using Altruist.Physx;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using Tests.Altruist.Physx.Fakes;
using static Tests.Altruist.Numerics.BitExact;

namespace Tests.Altruist.Gaming.TwoD;

/// <summary>Gameplay layer: each verb gives exactly the bits of the hand-written body update it
/// replaces (the inline form is spelled out in each test), and behaves as named.</summary>
public class GameplayVerbs2DTests
{
    private const float Dt = 1f / 60f;

    private static float InlineWrap(float a)
    {
        a = (a + MathF.PI) % (2 * MathF.PI);
        if (a < 0) a += 2 * MathF.PI;
        return a - MathF.PI;
    }

    private static float InlineApproach(float current, float target, float maxDelta) =>
        current < target ? MathF.Min(current + maxDelta, target) : MathF.Max(current - maxDelta, target);

    private static IEnumerable<(Vector2 V, Vector2 N, float K)> Cases()
    {
        var vs = Vectors(800).ToArray();
        var us = Units(800).ToArray();
        for (var i = 0; i < Math.Min(vs.Length, us.Length); i++) yield return (vs[i], us[i], (i % 13) * 0.17f - 0.6f);
    }

    [Fact]
    public void JumpOff_cancels_motion_into_the_surface_then_adds_the_jump()
    {
        foreach (var (v, n, _) in Cases())
        {
            float vx = v.X, vy = v.Y, nx = n.X, ny = n.Y;
            var into = vx * nx + vy * ny;
            if (into < 0) { vx -= nx * into; vy -= ny * into; }
            vx += nx * 11.5f;
            vy += ny * 11.5f;
            var body = new FakeBody2D { LinearVelocity = v };
            body.JumpOff(n, 11.5f);
            Same(body.LinearVelocity, new Vector2(vx, vy));
        }
        var b = new FakeBody2D { LinearVelocity = new Vector2(4, -9) };
        b.JumpOff(Vector2.UnitY, 10);
        b.LinearVelocity.Should().Be(new Vector2(4, 10));
    }

    [Fact]
    public void Push_launch_and_cancel_verbs()
    {
        foreach (var (v, n, k) in Cases())
        {
            var b = new FakeBody2D { LinearVelocity = v };
            b.PushAlong(n, k);
            Same(b.LinearVelocity, new Vector2(v.X + n.X * k, v.Y + n.Y * k));
            Same(b.LinearVelocity, v + n * k);

            b.LaunchAlong(n, 18);
            Same(b.LinearVelocity, new Vector2(n.X * 18, n.Y * 18));

            // A thrust that first cancels part of the opposing motion.
            float vx = v.X, vy = v.Y;
            var along = vx * n.X + vy * n.Y;
            if (along < 0) { vx -= n.X * along * 0.75f; vy -= n.Y * along * 0.75f; }
            b.LinearVelocity = v;
            b.CancelMotionAgainst(n, 0.75f);
            Same(b.LinearVelocity, new Vector2(vx, vy));
        }
        var r = new FakeBody2D { RotationZ = MathF.PI / 2 };
        r.LaunchForward(Vector2.UnitX, 5);
        r.LinearVelocity.X.Should().BeApproximately(0, 1e-5f);
        r.LinearVelocity.Y.Should().BeApproximately(5, 1e-5f);
    }

    [Fact]
    public void BounceOff_matches_the_tangent_keep_bounce()
    {
        foreach (var (v, n, _) in Cases())
        {
            var along = Vector2.Dot(v, n);
            var tangent = (v - n * along) * 0.4f;
            var b = new FakeBody2D { LinearVelocity = Vector2.One };
            b.BounceOff(n, 16, 0.4f, v); // from a recorded incoming velocity
            Same(b.LinearVelocity, n * 16 + tangent);
            b.LinearVelocity = v;
            b.BounceOff(n, 16, 0.4f); // from the current velocity (a launch pad)
            Same(b.LinearVelocity, tangent + n * 16);
        }
    }

    [Fact]
    public void AbsorbImpact_keeps_only_the_recoil_share()
    {
        foreach (var (v, n, k) in Cases())
        {
            var pre = n * 6;
            const float preW = -2f;
            var b = new FakeBody2D { LinearVelocity = v, AngularVelocityZ = k };
            b.AbsorbImpact(pre, preW, 0.2f);
            Same(b.LinearVelocity, pre + (v - pre) * 0.2f);
            Same(b.AngularVelocityZ, preW + (k - preW) * 0.2f);
        }
    }

    [Fact]
    public void Surface_driving_verbs_match_the_inline_tangent_frame()
    {
        foreach (var (v, n, k) in Cases())
        {
            float nx = n.X, ny = n.Y;
            float tx = ny, ty = -nx;
            var t = Direction2D.PerpendicularClockwise(n);
            Same(t, new Vector2(tx, ty));

            // Drive: approach the target speed along the tangent.
            float vx = v.X, vy = v.Y;
            var vt = vx * tx + vy * ty;
            var drag = 0.8f;
            var next = InlineApproach(vt, 20, 6f * drag * Dt);
            vx += tx * (next - vt);
            vy += ty * (next - vt);
            var b = new FakeBody2D { LinearVelocity = v };
            b.DriveAlong(t, 20, 6f * drag, Dt);
            Same(b.LinearVelocity, new Vector2(vx, vy));

            // Follow the surface: all speed along the tangent.
            var speed = MathF.Sqrt(vx * vx + vy * vy);
            var s2 = vx * tx + vy * ty >= 0 ? speed : -speed;
            vx = tx * s2;
            vy = ty * s2;
            b.FollowSurface(t);
            Same(b.LinearVelocity, new Vector2(vx, vy));

            // Stick to it.
            var stick = 25 + (vx * vx + vy * vy) / 14;
            vx -= nx * stick * Dt;
            vy -= ny * stick * Dt;
            var cur = b.LinearVelocity;
            b.StickTo(n, 25 + (cur.X * cur.X + cur.Y * cur.Y) / 14, Dt);
            Same(b.LinearVelocity, new Vector2(vx, vy));

            // Hold a speed along the normal (edge glide).
            float ex = v.X, ey = v.Y;
            var into = ex * nx + ey * ny;
            ex += nx * (k - into);
            ey += ny * (k - into);
            b.LinearVelocity = v;
            b.SetSpeedAlong(n, k);
            Same(b.LinearVelocity, new Vector2(ex, ey));

            // Brake along the tangent.
            b.LinearVelocity = v;
            b.BrakeAlong(t, 40, Dt);
            var bt = v.X * tx + v.Y * ty;
            var bn = InlineApproach(bt, 0, 40 * Dt);
            Same(b.LinearVelocity, new Vector2(v.X + tx * (bn - bt), v.Y + ty * (bn - bt)));
        }
    }

    [Fact]
    public void Speed_limit_drag_gravity_and_rolling_verbs()
    {
        foreach (var (v, n, k) in Cases())
        {
            var b = new FakeBody2D { LinearVelocity = v };
            b.ClampTopSpeed(25);
            var s = v.Length();
            Same(b.LinearVelocity, s > 25 ? v / s * 25 : v);

            b.LinearVelocity = v;
            b.ApplyDrag(0.3f, Dt);
            var drag = 1 - 0.3f * Dt;
            Same(b.LinearVelocity, new Vector2(v.X * drag, v.Y * drag));

            b.LinearVelocity = v;
            var speed = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
            b.BleedSpeed(0.5f);
            Same(b.LinearVelocity, speed == 0 ? v : new Vector2(v.X - v.X / speed * 0.5f, v.Y - v.Y / speed * 0.5f));

            b.LinearVelocity = v;
            b.Fall(30, Dt, k);
            var g = 30 * Dt;
            Same(b.LinearVelocity, new Vector2(v.X, v.Y - g * k));

            var slow = v / 30;
            b.LinearVelocity = slow;
            b.KeepRolling(1.5f, -1);
            var dir = slow.X != 0 ? MathF.Sign(slow.X) : -1;
            Same(b.LinearVelocity, MathF.Abs(slow.X) >= 1.5f ? slow : new Vector2(dir * 1.5f, slow.Y));
        }
    }

    [Fact]
    public void Orientation_verbs_match_inline_bits()
    {
        foreach (var (v, n, k) in Cases())
        {
            var rot = v.X / 4;
            var b = new FakeBody2D { RotationZ = rot, AngularVelocityZ = k * 5, Position = v };

            b.AlignToSurface(n, 14);
            Same(b.AngularVelocityZ, InlineWrap(MathF.Atan2(-n.X, n.Y) - rot) * 14);

            b.TurnToAngleIn(v.Y, 0.12f, Dt);
            Same(b.AngularVelocityZ, InlineWrap(v.Y - rot) / MathF.Max(0.12f, Dt));

            b.HoldAngle(v.Y, 8);
            Same(b.AngularVelocityZ, InlineWrap(v.Y - rot) * 8);

            b.AngularVelocityZ = k * 5;
            b.AimAt(v.Y, 10, 9, 70, Dt);
            var diff = InlineWrap(v.Y - rot);
            var raw = diff * 10;
            var targetW = raw < -9 ? -9 : raw > 9 ? 9 : raw;
            Same(b.AngularVelocityZ, InlineApproach(k * 5, targetW, 70 * Dt));

            b.AngularVelocityZ = k * 5;
            b.StopSpinning(70, Dt);
            float zero = 0;
            Same(b.AngularVelocityZ, InlineApproach(k * 5, zero, 70 * Dt));

            Same(b.UprightAngleOn(n), rot + InlineWrap(MathF.Atan2(-n.X, n.Y) - rot));
            Same(b.LevelAngle(), rot - InlineWrap(rot));

            // Snap upright onto a surface, sunk by a small drop.
            var drop = MathF.Abs(k) * 0.1f;
            var flat = rot + InlineWrap(MathF.Atan2(-n.X, n.Y) - rot);
            var p = b.Position - new Vector2(n.X, n.Y) * drop;
            b.SnapUprightOn(n, drop);
            Same(b.RotationZ, flat);
            Same(b.Position, p);
            b.AngularVelocityZ.Should().Be(0);
        }
    }

    [Fact]
    public void Prediction_and_reach_questions()
    {
        foreach (var (v, n, k) in Cases())
        {
            var b = new FakeBody2D { Position = n * 5, LinearVelocity = v };
            var t = MathF.Abs(k) + 0.1f;
            Same(b.PredictPosition(t, 30), new Vector2(b.Position.X + v.X * t, b.Position.Y + v.Y * t - 0.5f * 30 * t * t));
            Same(b.RiseLeft(30), v.Y > 0 ? v.Y * v.Y / (2 * 30f) : 0);
            var above = k * 20;
            var maxUp = v.Y > 0 ? v.Y * v.Y / (2 * 30f) : 0;
            b.CanReachHeight(above, 30, 1).Should().Be(!(above > maxUp + 1));
            Same(b.ImpactSpeedAgainst(n), -Vector2.Dot(v, n));
            Same(b.BrakingDistanceAlong(Vector2.UnitX, 35), v.X * v.X / (2 * 35f));
        }

        var ball = new FakeBody2D { Position = new Vector2(0, 5), LinearVelocity = new Vector2(10, 0) };
        var goal = new Aabb2D(8, 12, 0, 6);
        ball.WillEnter(goal, 10, 1.2f, 24).Should().BeTrue();
        ball.WillEnter(goal, 10, 0.5f, 24).Should().BeFalse();
        ball.WillEnter(goal, 10, 1.2f, 24, velocity: new Vector2(-10, 0)).Should().BeFalse();
        ball.WillEnter(new Aabb2D(8, 12, 6, 9), 0, 1.2f, 24, marginY: 1.5f).Should().BeTrue();

        var throwAt = new Vector2(12, 2);
        var vel = ball.VelocityToHit(throwAt, 9.8f, 1.1f);
        var lands = new FakeBody2D { Position = ball.Position, LinearVelocity = vel }.PredictPosition(1.1f, 9.8f);
        lands.X.Should().BeApproximately(12, 1e-4f);
        lands.Y.Should().BeApproximately(2, 1e-4f);

        var car = new FakeBody2D { Position = Vector2.Zero, LinearVelocity = new Vector2(-5, 0) };
        car.TimeToReach(new Vector2(20, 0), 10, 10).Should().Be(2.5f); // moving away counts as standing
        car.LinearVelocity = new Vector2(10, 0);
        car.TimeToReach(new Vector2(20, 0), 10, 10).Should().Be(2);

        var landing = new FakeBody2D { Position = new Vector2(0, 5), LinearVelocity = Vector2.Zero }.PredictLanding(10, 0.05f, 40, 1, 0.5f);
        landing.Position.Y.Should().BeGreaterThanOrEqualTo(1);

        var box = new FakeBody2D { Position = new Vector2(3, 0) };
        box.IsWithinBox(new Vector2(4.2f, 0.6f), 1, 0.5f, 0.3f, 0.2f).Should().BeTrue();
        box.IsWithinBox(new Vector2(4.2f, 0.6f), 1, 0.5f).Should().BeFalse();
        box.IsClearOfBox(new Vector2(7, 0), 1, 0.5f, 3).Should().BeTrue();
        box.IsClearOfBox(new Vector2(6.9f, 0), 1, 0.5f, 3).Should().BeFalse();
    }

    [Fact]
    public void ResetMotion_places_and_wakes_a_real_body()
    {
        using var world = PhysxWorldEngine2D.Create(new PhysxWorldSettings2D { Gravity = new Vector2(0, -10) });
        var ball = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Dynamic });
        world.CreateFixture(ball, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(1), Density = 1 });
        ball.IsAwake = false;
        ball.ResetMotion(new Vector2(3, 4), 0, new Vector2(1, 2), 0.5f);
        ball.Position.Should().Be(new Vector2(3, 4));
        ball.LinearVelocity.Should().Be(new Vector2(1, 2));
        ball.AngularVelocityZ.Should().Be(0.5f);
        ball.IsAwake.Should().BeTrue();
    }
}
