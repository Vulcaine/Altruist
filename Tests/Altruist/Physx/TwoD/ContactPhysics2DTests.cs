using System.Numerics;
using Altruist.Physx;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using Tests.Altruist.Physx.Fakes;
using static Tests.Altruist.Numerics.BitExact;

namespace Tests.Altruist.Physx.TwoD;

/// <summary>Physics layer, contacts and sweeps: ContactImpact2D and the frame body ops against a
/// designed striker-ball hit written inline, Velocity2D.ClosingSpeed / CentripetalAcceleration,
/// Kinematics.CentripetalAcceleration, Ballistics2D.FirstZoneEntered against hand-written sweeps.</summary>
public class ContactPhysics2DTests
{
    private static IEnumerable<ContactImpact2D> Impacts(int count = 1500)
    {
        var vs = Vectors(count, 30f, seed: 11).ToArray();
        var ps = Vectors(count, 4f, seed: 12).ToArray();
        var us = Units(count, seed: 13).ToArray();
        var ws = Floats(count, 12f, seed: 14).ToArray();
        for (var i = 0; i < count; i++)
        {
            var center = ps[(i + 7) % ps.Length];
            yield return new ContactImpact2D(ps[i], us[i % us.Length], vs[i], ws[i % ws.Length], center, vs[(i * 7 + 3) % vs.Length]);
        }
        // Zero velocities and spin, axis normals.
        yield return new ContactImpact2D(new Vector2(1, 0), Vector2.UnitX, Vector2.Zero, 0, Vector2.Zero, Vector2.Zero);
        yield return new ContactImpact2D(new Vector2(0, -0.5f), -Vector2.UnitY, new Vector2(-0f, 3), -0f, Vector2.Zero, new Vector2(0, -0f));
    }

    [Fact]
    public void ContactImpact_members_match_the_inline_hit_forms()
    {
        foreach (var imp in Impacts())
        {
            var n = imp.Normal;
            // Inline, as a designed hit computes it:
            var cp = Velocity2D.PointVelocity(imp.StrikerVelocity, imp.StrikerSpin, imp.Point - imp.StrikerCenter);
            var approach = Vector2.Dot(cp - imp.TargetVelocity, n);
            var carInto = Vector2.Dot(cp, n);
            var ballSpeed = imp.TargetVelocity.Length();
            var t = new Vector2(-n.Y, n.X);
            var ballT = Vector2.Dot(imp.TargetVelocity, t);
            var carT = Vector2.Dot(cp, t);

            Same(imp.StrikerPointVelocity, cp);
            Same(imp.StrikerPointVelocity, imp.StrikerVelocity + new Vector2(-imp.StrikerSpin * (imp.Point - imp.StrikerCenter).Y, imp.StrikerSpin * (imp.Point - imp.StrikerCenter).X));
            Same(imp.ClosingSpeed, approach);
            Same(imp.StrikerInto, carInto);
            Same(imp.TargetSpeed, ballSpeed);
            Same(imp.TargetSpeed, MathF.Sqrt(imp.TargetVelocity.X * imp.TargetVelocity.X + imp.TargetVelocity.Y * imp.TargetVelocity.Y));
            Same(imp.Frame.Tangent, t);
            Same(imp.TargetTangential, ballT);
            Same(imp.StrikerTangential, carT);
            Same(imp.Slip, carT - ballT);
        }
    }

    [Fact]
    public void Designed_hit_through_the_frame_ops_matches_the_inline_response()
    {
        var i = 0;
        foreach (var imp in Impacts())
        {
            // Tuning values vary per case.
            var restitution = 0.2f + (i % 5) * 0.1f;
            var mult = 0.8f + (i % 3) * 0.35f;
            var carry = 0.15f + (i % 4) * 0.05f;
            var speedCarry = 0.3f;
            var spinFactor = 0.55f;
            var radius = 1.4f;
            var w0 = (i % 9) * 0.7f - 3f;
            i++;

            // Inline response.
            var n = imp.Normal;
            var cp = Velocity2D.PointVelocity(imp.StrikerVelocity, imp.StrikerSpin, imp.Point - imp.StrikerCenter);
            var approach = Vector2.Dot(cp - imp.TargetVelocity, n);
            var ballSpeed = imp.TargetVelocity.Length();
            var t = new Vector2(-n.Y, n.X);
            var ballT = Vector2.Dot(imp.TargetVelocity, t);
            var carT = Vector2.Dot(cp, t);
            var outN = approach * (1 + restitution) * mult + ballSpeed * speedCarry;
            var outT = ballT * (1 - carry) + carT * carry;
            var expectedV = n * outN + t * outT;
            var expectedW = w0 + (carT - ballT) / radius * spinFactor;

            // Through the API.
            var ball = new FakeBody2D { LinearVelocity = imp.TargetVelocity, AngularVelocityZ = w0 };
            var aOutN = imp.ClosingSpeed * (1 + restitution) * mult + imp.TargetSpeed * speedCarry;
            var aOutT = imp.TargetTangential * (1 - carry) + imp.StrikerTangential * carry;
            ball.SetVelocityInFrame(imp.Frame, aOutN, aOutT);
            ball.AddSpinFromSlip(imp.Slip, radius, spinFactor);

            Same(ball.LinearVelocity, expectedV);
            Same(ball.AngularVelocityZ, expectedW);
        }
    }

    [Fact]
    public void Capture_reads_the_bodies_at_the_contact()
    {
        var car = new FakeBody2D { Position = new Vector2(2, 1), LinearVelocity = new Vector2(9, -1), AngularVelocityZ = 2.5f };
        var ball = new FakeBody2D { Position = new Vector2(3.5f, 1.2f), LinearVelocity = new Vector2(-4, 0.5f) };
        var imp = ContactImpact2D.Capture(new Vector2(3, 1.1f), Vector2.UnitX, car, ball);
        imp.StrikerVelocity.Should().Be(car.LinearVelocity);
        imp.StrikerSpin.Should().Be(2.5f);
        imp.StrikerCenter.Should().Be(((IPhysxBody2D)car).WorldCenter);
        imp.TargetVelocity.Should().Be(ball.LinearVelocity);
        // Head-on: closing 13 + spin, the car itself moves in at 9 + spin.
        imp.ClosingSpeed.Should().BeGreaterThan(imp.StrikerInto);
        imp.StrikerInto.Should().BeApproximately(9 + -2.5f * 0.1f, 1e-5f);
    }

    [Fact]
    public void ClosingSpeed_keeps_the_difference_first()
    {
        var vs = Vectors(2000, 40f).ToArray();
        var us = Units(2000).ToArray();
        for (var i = 1; i < Math.Min(vs.Length, us.Length); i++)
        {
            Vector2 va = vs[i], vb = vs[i - 1], nose = us[i];
            Same(Velocity2D.ClosingSpeed(va, vb, nose), Vector2.Dot(va - vb, nose));
            // The scalar form differs only in the sign of a zero result.
            var scalar = (va.X - vb.X) * nose.X + (va.Y - vb.Y) * nose.Y;
            if (scalar != 0) Same(Velocity2D.ClosingSpeed(va, vb, nose), scalar);
        }
    }

    [Fact]
    public void Centripetal_accelerations_match_inline()
    {
        foreach (var v in Vectors(2000, 40f))
        {
            Same(Velocity2D.CentripetalAcceleration(v, 12.5f), (v.X * v.X + v.Y * v.Y) / 12.5f);
            Same(Kinematics.CentripetalAcceleration(v.X, 7f), v.X * v.X / 7f);
        }
    }

    [Fact]
    public void FirstZoneEntered_matches_a_two_goal_sweep_with_a_floor()
    {
        var opp = new Aabb2D(52f, 58f, 0f, 8f);
        var own = new Aabb2D(-58f, -52f, 0f, 8f);
        const float r = 1.2f;
        const float g = 30f * 0.9f;
        const float h = 1f / 30;
        Span<Aabb2D> zones = stackalloc Aabb2D[] { opp.Grow(1, -0.3f), own.Grow(1.5f, 0.5f) };
        var ps = Vectors(2500, 50f, seed: 21).ToArray();
        var vs = Vectors(2500, 40f, seed: 22).ToArray();
        var hits = 0;
        for (var c = 0; c < ps.Length; c++)
        {
            var b = new Vector2(ps[c].X, MathF.Abs(ps[c].Y) * 0.3f + r);
            var v = vs[c];
            // Inline sweep.
            var expected = (Zone: -1, Step: -1);
            float x = b.X, y = b.Y, vy = v.Y;
            for (var i = 0; i < 60; i++)
            {
                vy -= g * h;
                x += v.X * h;
                y += vy * h;
                if (y < r) break;
                if (x >= opp.MinX - 1 && x <= opp.MaxX + 1 && y >= opp.MinY + 0.3f && y <= opp.MaxY - 0.3f) { expected = (0, i + 1); break; }
                if (x >= own.MinX - 1.5f && x <= own.MaxX + 1.5f && y >= own.MinY - 0.5f && y <= own.MaxY + 0.5f) { expected = (1, i + 1); break; }
            }
            var got = Ballistics2D.FirstZoneEntered(b, v, g, h, 60, zones, r);
            got.Should().Be(expected);
            if (got.Zone >= 0) hits++;
        }
        hits.Should().BeGreaterThan(20);
    }

    [Fact]
    public void FirstZoneEntered_without_a_floor_matches_a_single_zone_lookahead()
    {
        var goal = new Aabb2D(52f, 58f, 0f, 8f);
        const int steps = 48;
        const float g = 27f;
        var h = 1.25f / steps;
        var ps = Vectors(2000, 50f, seed: 31).ToArray();
        var vs = Vectors(2000, 45f, seed: 32).ToArray();
        for (var c = 0; c < ps.Length; c++)
        {
            Vector2 p = ps[c], vel = vs[c];
            var inline = false;
            float x = p.X, y = p.Y, vy = vel.Y;
            for (var i = 0; i < steps; i++)
            {
                vy -= g * h;
                x += vel.X * h;
                y += vy * h;
                if (x >= goal.MinX - 1.3f && x <= goal.MaxX + 1.3f && y >= goal.MinY && y <= goal.MaxY) { inline = true; break; }
            }
            (Ballistics2D.FirstZoneEntered(p, vel, g, h, steps, new[] { goal.Grow(1.3f, 0) }).Zone == 0).Should().Be(inline);
            (Ballistics2D.FirstStepWhere(p, vel, g, h, steps, q => goal.Contains(q.X, q.Y, 1.3f, 0)) >= 0).Should().Be(inline);
        }
        Ballistics2D.FirstZoneEntered(Vector2.Zero, Vector2.Zero, 10, 0.1f, 10, ReadOnlySpan<Aabb2D>.Empty).Should().Be((-1, -1));
    }
}
