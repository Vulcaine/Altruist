using System.Numerics;
using Altruist.Numerics;
using Altruist.Physx;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using Tests.Altruist.Physx.Fakes;
using static Tests.Altruist.Numerics.BitExact;

namespace Tests.Altruist.Physx.TwoD;

/// <summary>Physics layer: Velocity2D, Kinematics, Ballistics2D, ContactNormals2D,
/// BodyMotionExtensions2D, BodyState2D, ClosestRayHit2D — bit for bit against the inline
/// expressions they replace, plus behaviour on a real Box2D world.</summary>
public class PhysicsLayer2DTests
{
    private static float InlineWrap(float a)
    {
        a = (a + MathF.PI) % (2 * MathF.PI);
        if (a < 0) a += 2 * MathF.PI;
        return a - MathF.PI;
    }

    private static IEnumerable<(Vector2 V, Vector2 N, float K)> Cases()
    {
        var vs = Vectors(1500).ToArray();
        var us = Units(1500).ToArray();
        for (var i = 0; i < Math.Min(vs.Length, us.Length); i++) yield return (vs[i], us[i], (i % 11) * 0.21f - 0.5f);
    }

    // ── Velocity2D ────────────────────────────────────────────────────────

    [Fact]
    public void Velocity_ops_match_inline_bits()
    {
        foreach (var (v, n, k) in Cases())
        {
            float vx = v.X, vy = v.Y, nx = n.X, ny = n.Y;

            Same(Velocity2D.ApproachSpeed(v, n), -Vector2.Dot(v, n));

            // Cancel motion into a normal (amount 1), then add along it (a jump).
            {
                float x = vx, y = vy;
                var into = x * nx + y * ny;
                if (into < 0) { x -= nx * into; y -= ny * into; }
                Same(Velocity2D.CancelInto(v, n), new Vector2(x, y));
            }
            // Cancel a fraction of the opposing motion.
            {
                float x = vx, y = vy;
                var along = x * nx + y * ny;
                if (along < 0) { x -= nx * along * 0.8f; y -= ny * along * 0.8f; }
                Same(Velocity2D.CancelInto(v, n, 0.8f), new Vector2(x, y));
            }
            // Bounce / launch off a surface.
            {
                var along = Vector2.Dot(v, n);
                var tangent = (v - n * along) * 0.6f;
                Same(Velocity2D.Bounce(v, n, 14f, 0.6f), n * 14f + tangent);
                Same(Velocity2D.Bounce(v, n, 14f, 0.6f), tangent + n * 14f);
            }
            // Point velocity.
            {
                var r = n * 1.3f;
                Same(Velocity2D.PointVelocity(v, k, r), v + new Vector2(-k * r.Y, k * r.X));
            }
            // Press into a surface: v -= n * a * dt.
            {
                float x = vx, y = vy;
                var stick = 30f + (x * x + y * y) / 12f;
                x -= nx * stick * (1f / 60f);
                y -= ny * stick * (1f / 60f);
                Same(Velocity2D.AccelerateAlong(v, n, -(30f + (vx * vx + vy * vy) / 12f), 1f / 60f), new Vector2(x, y));
            }
            // Drag, gravity.
            {
                var drag = 1 - 0.35f * (1f / 60f);
                Same(Velocity2D.ApplyLinearDrag(v, 0.35f, 1f / 60f), new Vector2(vx * drag, vy * drag));
                var g = 30f * (1f / 60f);
                Same(Velocity2D.ApplyGravity(v, 30f, 1f / 60f, k), new Vector2(vx, vy - g * k));
                Same(Velocity2D.ApplyGravity(v, 30f, 1f / 60f), new Vector2(vx, vy - g));
            }
            // Keep rolling.
            {
                var min = 2f;
                var expected = v;
                if (MathF.Abs(vx) < min)
                {
                    var dir = vx != 0 ? MathF.Sign(vx) : -1;
                    expected = new Vector2(dir * min, vy);
                }
                Same(Velocity2D.KeepMinimumSpeedX(v / 20f, min, -1), MathF.Abs(vx / 20f) < min
                    ? new Vector2((vx / 20f != 0 ? MathF.Sign(vx / 20f) : -1) * min, vy / 20f)
                    : v / 20f);
                Same(Velocity2D.KeepMinimumSpeedX(v, min, -1), expected);
            }
        }
    }

    [Fact]
    public void Velocity_ops_semantics()
    {
        Velocity2D.CancelInto(new Vector2(3, -5), Vector2.UnitY).Should().Be(new Vector2(3, 0));
        Velocity2D.CancelInto(new Vector2(3, 5), Vector2.UnitY).Should().Be(new Vector2(3, 5));
        Velocity2D.CancelInto(new Vector2(3, -5), Vector2.UnitY, 0.5f).Should().Be(new Vector2(3, -2.5f));
        Velocity2D.Bounce(new Vector2(4, -10), Vector2.UnitY, 6, 0.5f).Should().Be(new Vector2(2, 6));
        Velocity2D.ApproachSpeed(new Vector2(0, -7), Vector2.UnitY).Should().Be(7);
        Velocity2D.PointVelocity(Vector2.Zero, 2, new Vector2(1, 0)).Should().Be(new Vector2(-0f, 2));
        Velocity2D.KeepMinimumSpeedX(new Vector2(0, 3), 2, -1).Should().Be(new Vector2(-2, 3));
        Velocity2D.KeepMinimumSpeedX(new Vector2(0.5f, 3), 2, -1).Should().Be(new Vector2(2, 3));
        Velocity2D.KeepMinimumSpeedX(new Vector2(float.NaN, 3), 2, -1).X.Should().Be(float.NaN); // never throws
    }

    // ── Kinematics ────────────────────────────────────────────────────────

    private static float InlineTimeToCover(float dist, float v0, float acc, float vmax)
    {
        float t = 0;
        var tAcc = MathF.Max(0, (vmax - v0) / acc);
        var dAcc = (v0 + vmax) / 2 * tAcc;
        if (dAcc >= dist) t += (-v0 + MathF.Sqrt(v0 * v0 + 2 * acc * dist)) / acc;
        else t += tAcc + (dist - dAcc) / vmax;
        return t;
    }

    [Fact]
    public void Kinematics_matches_inline_bits()
    {
        foreach (var x in Floats(3000, 60f))
        {
            var dist = MathF.Abs(x);
            var v0 = MathF.Abs(x * 0.37f) % 25f;
            Same(Kinematics.TimeToCover(dist, v0, 28f, 20f), InlineTimeToCover(dist, v0, 28f, 20f));
            Same(Kinematics.StoppingDistance(x, 40f), x * x / (2 * 40f));
        }
        Kinematics.TimeToCover(0, 0, 10, 10).Should().Be(0);
        Kinematics.TimeToCover(5, 0, 10, 10).Should().Be(1); // accelerates the whole way: max speed at the end
        Kinematics.TimeToCover(20, 0, 10, 10).Should().Be(2.5f); // 1 s to reach 10 (5 m), then 15 m at 10
        Kinematics.TimeToCover(10, 30, 10, 10).Should().Be(1); // above max speed: cruises at max speed
        Kinematics.StoppingDistance(10, 5).Should().Be(10);
    }

    // ── Ballistics2D ──────────────────────────────────────────────────────

    [Fact]
    public void Ballistics_closed_forms_match_inline_bits()
    {
        foreach (var (v, n, k) in Cases())
        {
            var p = n * 7;
            var t = MathF.Abs(k) + 0.25f;
            const float g = 30f;
            Same(Ballistics2D.PositionAt(p, v, g, t), new Vector2(p.X + v.X * t, p.Y + v.Y * t - 0.5f * g * t * t));
            Same(Ballistics2D.ApexHeight(v.Y, g), v.Y > 0 ? v.Y * v.Y / (2 * g) : 0);
            Same(Ballistics2D.LaunchSpeedY(v.Y, g, t), (v.Y + 0.5f * g * t * t) / t);
            var gx = v.X + 3;
            var gy = v.Y;
            var bx = p.X;
            var by = p.Y;
            var lv = Ballistics2D.LaunchVelocity(new Vector2(gx - bx, gy - by), g, t);
            Same(lv.Y, (gy - by + 0.5f * g * t * t) / t);
            Same(lv.X, (gx - bx) / t);
            Same(Ballistics2D.VelocityAt(v, g, t), new Vector2(v.X, v.Y - g * t));
        }
    }

    [Fact]
    public void Ballistics_launch_reaches_the_target()
    {
        var start = new Vector2(1, 2);
        var target = new Vector2(11, 5);
        var v = Ballistics2D.LaunchVelocity(target - start, 9.8f, 1.5f);
        var at = Ballistics2D.PositionAt(start, v, 9.8f, 1.5f);
        at.X.Should().BeApproximately(target.X, 1e-4f);
        at.Y.Should().BeApproximately(target.Y, 1e-4f);
        Ballistics2D.ApexHeight(-3, 9.8f).Should().Be(0);
        Ballistics2D.ApexHeight(9.8f, 9.8f).Should().BeApproximately(4.9f, 1e-5f);
    }

    [Fact]
    public void Ballistics_stepper_matches_inline_euler_and_floor_bounce()
    {
        var p0 = new Vector2(-3, 8);
        var v0 = new Vector2(11, 4);
        const float g = 30f, r = 1.2f, e = 0.6f;
        var t = 1.37f;
        var steps = Math.Max(1, (int)MathF.Round(t / 0.05f));
        var h = t / steps;

        float x = p0.X, y = p0.Y, vx = v0.X, vy = v0.Y;
        for (var i = 0; i < steps; i++)
        {
            vy -= g * h;
            x += vx * h;
            y += vy * h;
            if (y < r && vy < 0)
            {
                y = r;
                vy = -vy * e;
            }
        }
        var (p, v) = Ballistics2D.Predict(p0, v0, g, h, steps, r, e);
        Same(p, new Vector2(x, y));
        Same(v, new Vector2(vx, vy));

        float x2 = p0.X, y2 = p0.Y, vy2 = v0.Y;
        for (var i = 0; i < steps; i++) { vy2 -= g * h; x2 += v0.X * h; y2 += vy2 * h; }
        var (p2, v2) = Ballistics2D.Predict(p0, v0, g, h, steps);
        Same(p2, new Vector2(x2, y2));
        Same(v2.Y, vy2);

        var pos = p0;
        var vel = v0;
        Ballistics2D.BounceOnFloor(ref pos, ref vel, 100, 0.5f).Should().BeFalse("not falling");
        vel = new Vector2(0, -4);
        Ballistics2D.BounceOnFloor(ref pos, ref vel, 100, 0.5f).Should().BeTrue();
        pos.Y.Should().Be(100);
        vel.Y.Should().Be(2);
    }

    [Fact]
    public void Ballistics_FirstStepWhere_finds_the_first_entry()
    {
        var zone = new Aabb2D(9, 11, -100, 100);
        Ballistics2D.FirstStepWhere(Vector2.Zero, new Vector2(10, 0), 0, 0.1f, 20, p => zone.Contains(p)).Should().Be(9);
        Ballistics2D.FirstStepWhere(Vector2.Zero, new Vector2(10, 0), 0, 0.1f, 5, p => zone.Contains(p)).Should().Be(-1);
    }

    // ── ContactNormals2D ──────────────────────────────────────────────────

    [Fact]
    public void ContactNormals_sum_classify_and_normalize_like_inline()
    {
        var up = Vector2.Normalize(new Vector2(0.2f, 1));
        var normals = Units(40).ToArray();
        var acc = new ContactNormals2D();
        Vector2 any = default, wheels = default;
        bool hasAny = false, hasWheels = false;
        foreach (var n in normals)
        {
            acc.Add(n, up, 0.5f);
            hasAny = true;
            any += n;
            if (Vector2.Dot(n, up) >= 0.5f) { hasWheels = true; wheels += n; }
        }
        acc.HasAny.Should().Be(hasAny);
        acc.HasSupport.Should().Be(hasWheels);
        Same(acc.All, any);
        Same(acc.Support, wheels);
        var l = any.Length();
        if (l == 0) l = 1;
        Same(acc.AllDirection, new Vector2(any.X / l, any.Y / l));
        var lw = wheels.Length();
        if (lw == 0) lw = 1;
        Same(acc.SupportDirection, new Vector2(wheels.X / lw, wheels.Y / lw));

        var empty = default(ContactNormals2D);
        empty.HasAny.Should().BeFalse();
        empty.AllDirection.Should().Be(Vector2.Zero);
        var wall = new ContactNormals2D();
        wall.Add(Vector2.UnitX, Vector2.UnitY, 0.7f);
        wall.HasSupport.Should().BeFalse();
        wall.Add(Vector2.UnitY);
        wall.HasSupport.Should().BeFalse();
        wall.AllDirection.X.Should().BeApproximately(MathF.Sqrt(0.5f), 1e-6f);
    }

    // ── Body extensions (fake body: exact state round trip) ───────────────

    [Fact]
    public void Body_velocity_ops_write_the_math_layer_results()
    {
        foreach (var (v, n, k) in Cases().Take(400))
        {
            var b = new FakeBody2D { LinearVelocity = v, AngularVelocityZ = k, RotationZ = k * 3, Position = n * 2 };

            Same(b.SpeedAlong(n), v.X * n.X + v.Y * n.Y);

            b.LinearVelocity = v; b.AddVelocityAlong(n, k); Same(b.LinearVelocity, v + n * k);
            b.LinearVelocity = v; b.SetVelocityAlong(n, 3); Same(b.LinearVelocity, VectorMath2D.WithComponentAlong(v, n, 3));
            b.LinearVelocity = v; b.ApproachVelocityAlong(n, 20, 26f * 1.5f, 1f / 60f);
            Same(b.LinearVelocity, VectorMath2D.ApproachComponentAlong(v, n, 20, 26f * 1.5f * (1f / 60f)));
            b.LinearVelocity = v; b.RedirectVelocityAlong(n); Same(b.LinearVelocity, VectorMath2D.RedirectAlong(v, n));
            b.LinearVelocity = v; b.AccelerateAlong(n, -9, 1f / 60f); Same(b.LinearVelocity, Velocity2D.AccelerateAlong(v, n, -9, 1f / 60f));
            b.LinearVelocity = v; b.CancelVelocityInto(n, 0.7f); Same(b.LinearVelocity, Velocity2D.CancelInto(v, n, 0.7f));
            b.LinearVelocity = v; b.BounceVelocity(n, 5, 0.3f); Same(b.LinearVelocity, Velocity2D.Bounce(v, n, 5, 0.3f));
            b.LinearVelocity = v; b.ApplyLinearDrag(0.4f, 1f / 60f); Same(b.LinearVelocity, Velocity2D.ApplyLinearDrag(v, 0.4f, 1f / 60f));
            b.LinearVelocity = v; b.ApplyGravity(30, 1f / 60f, 0.5f); Same(b.LinearVelocity, Velocity2D.ApplyGravity(v, 30, 1f / 60f, 0.5f));

            b.LinearVelocity = v; b.ClampSpeed(20);
            var s = v.Length();
            Same(b.LinearVelocity, s > 20 ? v / s * 20 : v);

            b.LinearVelocity = v; b.ReduceSpeed(0.25f);
            var speed = MathF.Sqrt(v.X * v.X + v.Y * v.Y);
            Same(b.LinearVelocity, speed == 0 ? v : new Vector2(v.X - v.X / speed * 0.25f, v.Y - v.Y / speed * 0.25f));

            // Recoil blend.
            var pre = n * 4;
            const float preW = 1.5f;
            b.LinearVelocity = v; b.AngularVelocityZ = k;
            b.BlendMotionFrom(pre, preW, 0.35f);
            Same(b.LinearVelocity, pre + (v - pre) * 0.35f);
            Same(b.AngularVelocityZ, preW + (k - preW) * 0.35f);

            // Point velocity.
            var point = n * 3 + Vector2.UnitX;
            b.LinearVelocity = v; b.AngularVelocityZ = k;
            var r = point - b.Position;
            Same(b.VelocityAtPoint(point), v + new Vector2(-k * r.Y, k * r.X));
        }
    }

    [Fact]
    public void Body_angular_ops_match_inline_bits()
    {
        foreach (var (v, n, k) in Cases().Take(800))
        {
            var rot = v.X / 3;
            var b = new FakeBody2D { RotationZ = rot, AngularVelocityZ = k * 4 };

            b.AlignUpToNormal(n, 12);
            Same(b.AngularVelocityZ, InlineWrap(MathF.Atan2(-n.X, n.Y) - rot) * 12);

            b.ArriveAtAngle(v.Y, MathF.Abs(k) * 0.1f, 1f / 60f);
            Same(b.AngularVelocityZ, InlineWrap(v.Y - rot) / MathF.Max(MathF.Abs(k) * 0.1f, 1f / 60f));

            b.RotateTowardAngle(v.Y, 6);
            Same(b.AngularVelocityZ, InlineWrap(v.Y - rot) * 6);

            b.AngularVelocityZ = k * 4;
            b.SteerAngularVelocity(v.Y, 10, 9, 60, 1f / 60f);
            var diff = InlineWrap(v.Y - rot);
            var raw = diff * 10;
            var targetW = raw < -9 ? -9 : raw > 9 ? 9 : raw;
            var w0 = k * 4;
            var step = 60 * (1f / 60f);
            Same(b.AngularVelocityZ, w0 < targetW ? MathF.Min(w0 + step, targetW) : MathF.Max(w0 - step, targetW));

            b.AngularVelocityZ = k * 4;
            b.ApproachAngularVelocity(0, 60, 1f / 60f);
            Same(b.AngularVelocityZ, w0 < 0 ? MathF.Min(w0 + step, 0) : MathF.Max(w0 - step, 0));
        }
    }

    [Fact]
    public void Body_box_queries_use_local_points()
    {
        var b = new FakeBody2D { Position = new Vector2(10, 0), RotationZ = MathF.PI / 2 };
        // Rotated 90°: the box's local X runs along world +Y.
        b.BoxContains(new Vector2(10, 1.9f), 2, 0.5f).Should().BeTrue();
        b.BoxContains(new Vector2(11.9f, 0), 2, 0.5f).Should().BeFalse();
        b.BoxContains(new Vector2(11.9f, 0), 2, 0.5f, padY: 1.5f).Should().BeTrue();
        b.BoxDistanceSquared(new Vector2(10, 5), 2, 0.5f).Should().BeApproximately(9, 1e-4f);
        var local = ((IPhysxBody2D)b).GetLocalPoint(new Vector2(12, 1));
        b.BoxContains(new Vector2(12, 1), 1.1f, 0.45f, 0.3f, 0.4f)
            .Should().Be(!(MathF.Abs(local.X) > 1.1f + 0.3f || MathF.Abs(local.Y) > 0.45f + 0.4f));
    }

    [Fact]
    public void KeepMinimumSpeedX_writes_only_when_needed()
    {
        var b = new CountingBody { LinearVelocity = new Vector2(5, 1) };
        b.Writes = 0;
        b.KeepMinimumSpeedX(2, 1);
        b.Writes.Should().Be(0);
        b.LinearVelocity = new Vector2(0, 1);
        b.Writes = 0;
        b.KeepMinimumSpeedX(2, 1);
        b.Writes.Should().Be(1);
        b.LinearVelocity.Should().Be(new Vector2(2, 1));
        b.Writes = 0;
        b.ClampSpeed(100);
        b.Writes.Should().Be(0);
    }

    private sealed class CountingBody : FakeBody2D
    {
        private Vector2 _v;
        public int Writes;
        public override Vector2 LinearVelocity
        {
            get => _v;
            set { _v = value; Writes++; }
        }
    }

    // ── State ─────────────────────────────────────────────────────────────

    [Fact]
    public void BodyState_and_CopyMotionFrom_on_a_real_world()
    {
        using var world = PhysxWorldEngine2D.Create(new PhysxWorldSettings2D { Gravity = new Vector2(0, -10) });
        var a = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Dynamic, Position = new Vector2(1, 5), Angle = 0.3f });
        world.CreateFixture(a, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(0.5f), Density = 1 });
        var b = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Dynamic, Position = new Vector2(-4, 2) });
        world.CreateFixture(b, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(0.5f), Density = 1 });
        a.LinearVelocity = new Vector2(3, 4);
        a.AngularVelocityZ = 2;

        var saved = a.CaptureState();
        for (var i = 0; i < 30; i++) world.Step(1f / 60f);
        a.Position.Should().NotBe(saved.Position);
        saved.Restore(a);
        Same(a.Position, saved.Position);
        Same(a.RotationZ, saved.Angle);
        Same(a.LinearVelocity, saved.LinearVelocity);
        Same(a.AngularVelocityZ, saved.AngularVelocity);
        a.IsAwake.Should().BeTrue();

        b.CopyMotionFrom(a);
        Same(b.Position, a.Position);
        Same(b.RotationZ, a.RotationZ);
        Same(b.LinearVelocity, a.LinearVelocity);
        Same(b.AngularVelocityZ, a.AngularVelocityZ);

        var off = saved with { IsEnabled = false, IsAwake = false };
        off.Restore(b, wake: false, restoreEnabled: true);
        b.IsEnabled.Should().BeFalse();
        new BodyState2D(Vector2.Zero, 0, Vector2.Zero, 0).Restore(b, restoreEnabled: true);
        b.IsEnabled.Should().BeTrue();
    }

    // ── Ray casts ─────────────────────────────────────────────────────────

    [Fact]
    public void ClosestRayHit_keeps_the_nearest_accepted_hit_over_several_rays()
    {
        using var world = PhysxWorldEngine2D.Create(new PhysxWorldSettings2D());
        var floor = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Static });
        world.CreateFixture(floor, new PhysxFixtureDef2D { Shape = PhysxShape2D.Box(50, 0.5f), UserData = "solid" });
        var glass = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Static, Position = new Vector2(0, 3) });
        world.CreateFixture(glass, new PhysxFixtureDef2D { Shape = PhysxShape2D.Box(1, 0.1f), UserData = "glass" });
        var shelf = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Static, Position = new Vector2(4, 2) });
        world.CreateFixture(shelf, new PhysxFixtureDef2D { Shape = PhysxShape2D.Box(1, 0.1f), UserData = "solid" });

        var hit = new ClosestRayHit2D(10, f => (string?)f.UserData == "solid");
        world.RayCast(new Vector2(0, 10), new Vector2(0, 0), hit); // passes through the glass, hits the floor
        hit.Found.Should().BeTrue();
        hit.Point.Y.Should().BeApproximately(0.5f, 1e-3f);
        hit.Normal.Y.Should().BeApproximately(1, 1e-3f);
        Same(hit.Distance, hit.Fraction * 10);
        world.RayCast(new Vector2(4, 10), new Vector2(4, 0), hit); // the shelf is closer: kept
        hit.Point.Y.Should().BeApproximately(2.1f, 1e-3f);
        hit.Fixture!.Body.Should().BeSameAs(shelf);

        hit.Reset();
        hit.Found.Should().BeFalse();
        hit.Distance.Should().Be(float.PositiveInfinity);

        var any = world.RayCastClosest(new Vector2(0, 10), new Vector2(0, 0));
        any.Found.Should().BeTrue();
        any.Point.Y.Should().BeApproximately(3.1f, 1e-3f); // the glass, unfiltered
        any.Distance.Should().BeApproximately(6.9f, 1e-3f);

        world.RayCastClosest(new Vector2(-20, 10), new Vector2(-20, 5)).Found.Should().BeFalse();
    }

    [Fact]
    public void ClosestRayHit_callback_contract()
    {
        var hit = new ClosestRayHit2D(4, _ => false);
        hit.OnHit(null!, Vector2.Zero, Vector2.UnitY, 0.5f).Should().Be(-1);
        hit.Found.Should().BeFalse();
        hit.Filter = null;
        hit.OnHit(null!, new Vector2(1, 2), Vector2.UnitY, 0.5f).Should().Be(0.5f);
        hit.OnHit(null!, new Vector2(1, 3), Vector2.UnitY, 0.75f).Should().Be(0.75f);
        hit.Distance.Should().Be(2);
        hit.Point.Should().Be(new Vector2(1, 2));
    }
}
