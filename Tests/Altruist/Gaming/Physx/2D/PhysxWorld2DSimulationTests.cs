using System.Numerics;
using Altruist.Physx;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Tests.Gaming.Physx.TwoD;

/// <summary>The standalone 2D world API: bodies, fixtures, filters, contacts, ray casts.</summary>
public class PhysxWorld2DSimulationTests
{
    private static IPhysxWorldEngine2D World(float gravity = -10f) =>
        PhysxWorldEngine2D.Create(new PhysxWorldSettings2D { Gravity = new Vector2(0, gravity), VelocityIterations = 8, PositionIterations = 3 });

    private static IPhysxBody2D Ground(IPhysxWorldEngine2D world, object? tag = null)
    {
        var ground = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Static });
        world.CreateFixture(ground, new PhysxFixtureDef2D { Shape = PhysxShape2D.Box(50, 0.5f), UserData = tag });
        return ground;
    }

    private static IPhysxBody2D Crate(IPhysxWorldEngine2D world, Vector2 at, PhysxFilter2D? filter = null, object? tag = null)
    {
        var body = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Dynamic, Position = at, UserData = tag });
        world.CreateFixture(body, new PhysxFixtureDef2D { Shape = PhysxShape2D.Box(0.5f, 0.5f), Density = 1, Filter = filter ?? PhysxFilter2D.Default });
        return body;
    }

    private static void Run(IPhysxWorldEngine2D world, int steps)
    {
        for (var i = 0; i < steps; i++) world.Step(1f / 60f);
    }

    [Fact]
    public void A_falling_body_lands_on_a_static_box()
    {
        var world = World();
        Ground(world);
        var crate = Crate(world, new Vector2(0, 5));
        Run(world, 180);
        Assert.InRange(crate.Position.Y, 0.9f, 1.1f);
        Assert.Equal(2, world.Bodies.Count);
    }

    [Fact]
    public void Default_fixtures_collide_with_each_other()
    {
        var world = World();
        Ground(world);
        Crate(world, new Vector2(0, 1));
        var top = Crate(world, new Vector2(0, 3));
        Run(world, 180);
        Assert.InRange(top.Position.Y, 1.9f, 2.1f);
    }

    [Fact]
    public void A_shared_negative_group_never_collides()
    {
        var world = World();
        Ground(world);
        var ghost = new PhysxFilter2D(Category: 1, Mask: 0xFFFF, Group: -1);
        Crate(world, new Vector2(0, 1), ghost);
        var top = Crate(world, new Vector2(0, 3), ghost);
        Run(world, 180);
        Assert.InRange(top.Position.Y, 0.9f, 1.1f);
    }

    [Fact]
    public void Body_and_fixture_user_data_reach_the_contact_listener()
    {
        var world = World();
        Ground(world, tag: "floor");
        Crate(world, new Vector2(0, 2), tag: "crate");
        var seen = new List<(object?, object?)>();
        world.SetContactListener(new Recorder(c => seen.Add((c.FixtureA.UserData ?? c.BodyA.UserData, c.FixtureB.UserData ?? c.BodyB.UserData))));
        Run(world, 120);
        Assert.Contains(seen, p => (p.Item1, p.Item2) is ("floor", "crate") or ("crate", "floor"));
    }

    [Fact]
    public void Pre_solve_can_disable_a_contact_and_report_its_manifold()
    {
        var world = World();
        Ground(world);
        var crate = Crate(world, new Vector2(0, 2));
        PhysxWorldManifold2D? manifold = null;
        world.SetContactListener(new Recorder(c =>
        {
            manifold ??= c.PointCount > 0 ? c.GetWorldManifold() : null;
            c.IsEnabled = false;
        }));
        Run(world, 120);
        Assert.True(crate.Position.Y < 0, "a disabled contact lets the body fall through");
        Assert.NotNull(manifold);
        Assert.Equal(1f, MathF.Abs(manifold!.Value.Normal.Y), 3);
        Assert.InRange(manifold.Value.PointCount, 1, 2);
    }

    [Fact]
    public void Contacts_enumerates_touching_contacts()
    {
        var world = World();
        Ground(world);
        Crate(world, new Vector2(0, 1));
        Run(world, 120);
        var touching = world.Contacts.Count(c => c.IsTouching && c.IsEnabled);
        Assert.Equal(1, touching);
    }

    [Fact]
    public void Ray_cast_callback_finds_the_closest_tagged_fixture()
    {
        var world = World(0);
        Ground(world, tag: "floor");
        var near = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Static, Position = new Vector2(0, 3) });
        world.CreateFixture(near, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(0.5f), UserData = "ignored" });
        var hit = new ClosestTagged("floor");
        world.RayCast(new Vector2(0, 10), new Vector2(0, -10), hit);
        Assert.True(hit.Found);
        Assert.Equal(0.5f, hit.Point.Y, 3);
        Assert.Equal(1f, hit.Normal.Y, 3);
    }

    [Fact]
    public void Setting_mass_scales_mass_and_keeps_the_shape()
    {
        var world = World();
        var body = Crate(world, Vector2.Zero);
        body.Mass = 7f;
        Assert.Equal(7f, body.Mass, 4);
    }

    [Fact]
    public void A_clockwise_chain_loop_keeps_bodies_inside()
    {
        var world = World();
        var walls = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Static });
        world.CreateFixture(walls, new PhysxFixtureDef2D
        {
            Shape = PhysxShape2D.Chain(new[] { new Vector2(-5, 0), new Vector2(-5, 10), new Vector2(5, 10), new Vector2(5, 0) }, loop: true),
        });
        var ball = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Dynamic, Position = new Vector2(0, 5), Bullet = true });
        world.CreateFixture(ball, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(0.3f), Density = 1, Restitution = 0.5f });
        ball.LinearVelocity = new Vector2(40, -30);
        Run(world, 240);
        Assert.InRange(ball.Position.X, -5f, 5f);
        Assert.InRange(ball.Position.Y, 0f, 10f);
    }

    [Fact]
    public void Body_frame_conversions_round_trip()
    {
        var world = World(0);
        var body = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Dynamic, Position = new Vector2(2, 3), Angle = MathF.PI / 2 });
        var up = body.GetWorldVector(new Vector2(1, 0));
        Assert.Equal(0f, up.X, 4);
        Assert.Equal(1f, up.Y, 4);
        var p = body.GetWorldPoint(new Vector2(1, 0));
        var local = body.GetLocalPoint(p);
        Assert.Equal(1f, local.X, 4);
        Assert.Equal(0f, local.Y, 4);
        body.SetTransform(new Vector2(-1, -1), 0);
        Assert.Equal(new Vector2(-1, -1), body.Position);
        Assert.Equal(0f, body.RotationZ);
    }

    [Fact]
    public void Rotation2D_rotates_and_unrotates()
    {
        var r = Rotation2D.FromDegrees(90);
        var v = r.Rotate(new Vector2(1, 0));
        Assert.Equal(0f, v.X, 5);
        Assert.Equal(1f, v.Y, 5);
        var back = r.Unrotate(v);
        Assert.Equal(1f, back.X, 5);
        Assert.Equal(0f, back.Y, 5);
    }

    [Fact]
    public void Identical_worlds_stay_bit_identical()
    {
        static float[] Simulate()
        {
            var world = World();
            Ground(world);
            var bodies = Enumerable.Range(0, 6).Select(i => Crate(world, new Vector2(i * 0.3f - 1, 2 + i * 1.1f))).ToList();
            Run(world, 300);
            return bodies.SelectMany(b => new[] { b.Position.X, b.Position.Y, b.RotationZ }).ToArray();
        }
        Assert.Equal(Simulate(), Simulate());
    }

    private sealed class Recorder(Action<IPhysxContact2D> preSolve) : IPhysxContactListener2D
    {
        public void PreSolve(IPhysxContact2D contact) => preSolve(contact);
    }

    private sealed class ClosestTagged(string tag) : IPhysxRayCastCallback2D
    {
        public bool Found;
        public Vector2 Point;
        public Vector2 Normal;

        public float OnHit(IPhysxFixture2D fixture, Vector2 point, Vector2 normal, float fraction)
        {
            if (!Equals(fixture.UserData, tag)) return -1;
            Found = true;
            Point = point;
            Normal = normal;
            return fraction;
        }
    }
}
