using System.Numerics;
using Altruist.Gaming.TwoD;
using Altruist.Physx.TwoD;
using FluentAssertions;
using Tests.Altruist.Physx.Fakes;
using static Tests.Altruist.Numerics.BitExact;

namespace Tests.Altruist.Gaming.TwoD;

/// <summary>The configured body responses give exactly the bits of the hand-written rebound and
/// designed-hit code they replace (the inline form is spelled out in each test).</summary>
public class ImpactResponses2DTests
{
    private static readonly SurfaceRebound2D Rebound = new() { MinImpactSpeed = 3, Restitution = 0.9f, MinOutSpeed = 16, TangentKeep = 0.9f };
    private static readonly StrikeResponse2D Response = new() { Restitution = 0.1f, SpeedCarry = 0.45f, SurfaceCarry = 0.35f, SpinFactor = 0.5f };

    private static IEnumerable<ContactImpact2D> Impacts()
    {
        var rng = new Random(7);
        float R(float span) => (float)(rng.NextDouble() * 2 - 1) * span;
        for (var i = 0; i < 400; i++)
        {
            var n = Vector2.Normalize(new Vector2(R(1), R(1)) + new Vector2(1e-3f, 0));
            yield return new ContactImpact2D(new Vector2(R(20), R(10)), n, new Vector2(R(30), R(30)), R(8),
                new Vector2(R(20), R(10)), new Vector2(R(40), R(40)));
        }
    }

    [Fact]
    public void Rebound_matches_the_inline_dash_rebound()
    {
        foreach (var imp in Impacts())
        {
            var speed = Velocity2D.ApproachSpeed(imp.StrikerVelocity, imp.Normal);
            Rebound.IsImpact(speed).Should().Be(!(speed < 3));
            var outSpeed = Rebound.OutSpeed(speed) * 0.85f * 1.4f;
            Same(outSpeed, MathF.Max(speed * 0.9f, 16) * 0.85f * 1.4f);

            var body = new FakeBody2D { LinearVelocity = Vector2.One };
            Rebound.Apply(body, imp.Normal, outSpeed, imp.StrikerVelocity);
            var inline = new FakeBody2D { LinearVelocity = Vector2.One };
            inline.BounceOff(imp.Normal, outSpeed, 0.9f, imp.StrikerVelocity);
            Same(body.LinearVelocity, inline.LinearVelocity);
        }
    }

    [Fact]
    public void Strike_matches_the_inline_designed_hit()
    {
        foreach (var imp in Impacts())
        {
            const float mult = 1.3f, combo = 1.2f, radius = 1.25f, w0 = -0.7f;
            var bounce = imp.ClosingSpeed * (1 + 0.1f);
            Same(Response.Rebound(imp), bounce);

            var ball = new FakeBody2D { LinearVelocity = imp.TargetVelocity, AngularVelocityZ = w0 };
            Response.Strike(ball, imp, bounce * mult * combo, radius);

            var outN = bounce * mult * combo + imp.TargetSpeed * 0.45f;
            var outT = imp.TargetTangential * (1 - 0.35f) + imp.StrikerTangential * 0.35f;
            Same(ball.LinearVelocity, imp.Frame.Compose(outN, outT));
            Same(ball.AngularVelocityZ, w0 + imp.Slip / radius * 0.5f);
        }
    }

    [Fact]
    public void Launch_sets_exactly_the_given_normal_speed()
    {
        foreach (var imp in Impacts())
        {
            var outN = MathF.Max(imp.TargetSpeed, Response.Rebound(imp)) * 1.8f;
            var ball = new FakeBody2D { LinearVelocity = imp.TargetVelocity };
            Response.Launch(ball, imp, outN, 1.25f);
            Same(ball.LinearVelocity, imp.Frame.Compose(outN, imp.TargetTangential * (1 - 0.35f) + imp.StrikerTangential * 0.35f));
        }
    }
}
