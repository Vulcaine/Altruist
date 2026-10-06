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

    [Fact]
    public void The_zero_filter_is_the_default_filter()
    {
        Assert.Equal(PhysxFilter2D.Default, default(PhysxFilter2D));
        Assert.Equal(PhysxFilter2D.Default, new PhysxFilter2D());
        Assert.Equal(PhysxFilter2D.Default.GetHashCode(), default(PhysxFilter2D).GetHashCode());
        var f = default(PhysxFilter2D);
        Assert.Equal((ushort)1, f.Category);
        Assert.Equal((ushort)0xFFFF, f.Mask);
        Assert.Equal((short)0, f.Group);

        var custom = new PhysxFilter2D(Category: 4, Mask: 0, Group: -2) with { Mask = 2 };
        var (category, mask, group) = custom;
        Assert.Equal((4, 2, -2), (category, mask, group));
        Assert.Equal(custom, new PhysxFilter2D(4, 2, -2));
        Assert.NotEqual(PhysxFilter2D.Default, custom);

        // A fixture with the zero filter collides like the default one.
        var world = World();
        Ground(world);
        Crate(world, new Vector2(0, 1), default(PhysxFilter2D));
        var top = Crate(world, new Vector2(0, 3), new PhysxFilter2D());
        Run(world, 180);
        Assert.InRange(top.Position.Y, 1.9f, 2.1f);
    }

    [Fact]
    public void The_zero_body_def_is_a_static_sleeping_body_with_full_gravity()
    {
        var def = default(PhysxBodyDef2D);
        Assert.Equal(PhysxBodyType.Static, def.Type);
        Assert.True(def.AllowSleep);
        Assert.Equal(1f, def.GravityScale);
        Assert.Equal(def, new PhysxBodyDef2D());
        Assert.Equal(0.1f, new PhysxBodyDef2D { GravityScale = 0.1f }.GravityScale);
        Assert.Equal(0f, new PhysxBodyDef2D { GravityScale = 0f }.GravityScale);
        Assert.False(new PhysxBodyDef2D { AllowSleep = false }.AllowSleep);

        var world = World();
        var body = world.CreateBody(default(PhysxBodyDef2D) with { Type = PhysxBodyType.Dynamic, Position = new Vector2(0, 10) });
        world.CreateFixture(body, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(0.5f), Density = 1 });
        Run(world, 30);
        Assert.True(body.Position.Y < 9.5f, "a zero-value body def falls under gravity");
    }

    private static (float X, float Y, float Vy) Drop(PhysxWorldSettings2D settings, params float[] steps)
    {
        var world = PhysxWorldEngine2D.Create(settings);
        Ground(world);
        var crate = Crate(world, new Vector2(0.3f, 4));
        crate.AngularVelocityZ = 1f;
        foreach (var dt in steps) world.Step(dt);
        return (crate.Position.X, crate.Position.Y, crate.LinearVelocity.Y);
    }

    [Fact]
    public void Step_advances_by_dt_unless_fixed_stepping_is_opted_in()
    {
        var gravity = new Vector2(0, -10);
        var fixedSteps = new PhysxWorldSettings2D { Gravity = gravity, FixedDeltaTime = 1f / 60f, MaxSubSteps = 4 };
        var sixtieth = Enumerable.Repeat(1f / 60f, 40).ToArray();

        // Default (no sub-steps): FixedDeltaTime does not change stepping.
        Assert.Equal(
            Drop(new PhysxWorldSettings2D { Gravity = gravity, FixedDeltaTime = 1f / 10f }, Enumerable.Repeat(1f / 30f, 20).ToArray()),
            Drop(new PhysxWorldSettings2D { Gravity = gravity }, Enumerable.Repeat(1f / 30f, 20).ToArray()));

        // Fixed stepping: a 1/30 step is two 1/60 steps, two 1/120 steps are one.
        Assert.Equal(Drop(new PhysxWorldSettings2D { Gravity = gravity }, sixtieth),
            Drop(fixedSteps, Enumerable.Repeat(1f / 30f, 20).ToArray()));
        Assert.Equal(Drop(new PhysxWorldSettings2D { Gravity = gravity }, sixtieth),
            Drop(fixedSteps, Enumerable.Repeat(1f / 120f, 80).ToArray()));

        // At most MaxSubSteps per call: a one-second hitch runs 4 steps, not 60.
        Assert.Equal(Drop(new PhysxWorldSettings2D { Gravity = gravity }, 1f / 60f, 1f / 60f, 1f / 60f, 1f / 60f),
            Drop(fixedSteps, 1f));
    }

    [Fact]
    public void Ray_cast_returns_the_closest_hits_sorted_by_fraction()
    {
        var world = World(0);
        // Created out of order so the broad-phase does not report them front to back.
        foreach (var x in new[] { 7, 2, 9, 4, 1, 8, 3, 6, 5 })
        {
            var b = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Static, Position = new Vector2(x, 0), UserData = x });
            world.CreateFixture(b, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(0.25f) });
        }
        var ray = new PhysxRay2D(new Vector2(0, 0), new Vector2(10, 0));

        var three = world.RayCast(ray, 3).ToList();
        Assert.Equal(new object?[] { 1, 2, 3 }, three.Select(h => h.Body.UserData));
        Assert.True(three[0].Fraction <= three[1].Fraction && three[1].Fraction <= three[2].Fraction);

        var closest = Assert.Single(world.RayCast(ray));
        Assert.Equal(1, closest.Body.UserData);
        Assert.Equal(0.75f, closest.Point.X, 3);

        var all = world.RayCast(ray, 100).ToList();
        Assert.Equal(Enumerable.Range(1, 9).Cast<object?>(), all.Select(h => h.Body.UserData));

        var back = world.RayCast(new PhysxRay2D(new Vector2(10, 0), new Vector2(0, 0)), 2).ToList();
        Assert.Equal(new object?[] { 9, 8 }, back.Select(h => h.Body.UserData));
    }

    [Fact]
    public void Enumerated_contacts_are_distinct_and_fail_loudly_once_stale()
    {
        var world = World();
        Ground(world, tag: "floor");
        var crates = Enumerable.Range(0, 3).Select(i => Crate(world, new Vector2(i * 3 - 3, 1), tag: i)).ToList();
        Run(world, 60);

        var contacts = world.Contacts.Where(c => c.IsTouching).ToList();
        Assert.Equal(3, contacts.Count);
        Assert.Equal(3, contacts.Distinct().Count());
        var tags = contacts.Select(c => c.BodyA.UserData ?? c.BodyB.UserData).OrderBy(t => (int)t!).ToList();
        Assert.Equal(new object?[] { 0, 1, 2 }, tags);
        var infos = contacts.Select(c => c.ToInfo()).ToList();

        world.Step(1f / 60f);
        Assert.Throws<InvalidOperationException>(() => contacts[0].IsTouching);
        // The copies stay valid.
        Assert.All(infos, i => Assert.True(i.IsTouching));
        Assert.All(infos, i => Assert.Equal(1f, MathF.Abs(i.Manifold.Normal.Y), 3));
        Assert.Equal(3, infos.Select(i => i.BodyA.UserData ?? i.BodyB.UserData).Distinct().Count());

        // Removing a body also invalidates views taken before.
        var before = world.Contacts.First();
        world.RemoveBody(crates[0]);
        Assert.Throws<InvalidOperationException>(() => before.PointCount);
    }

    [Fact]
    public void A_listener_contact_is_valid_only_inside_the_callback_but_can_be_copied()
    {
        var world = World();
        Ground(world, tag: "floor");
        Crate(world, new Vector2(0, 2), tag: "crate");
        var kept = new List<IPhysxContact2D>();
        var copies = new List<PhysxContactInfo2D>();
        world.SetContactListener(new BeginRecorder(c =>
        {
            kept.Add(c);
            copies.Add(c.ToInfo());
        }));
        Run(world, 120);
        Assert.Single(copies);
        Assert.Throws<InvalidOperationException>(() => kept[0].IsTouching);
        Assert.True(copies[0].IsTouching);
        Assert.Contains(copies[0].FixtureA.UserData ?? copies[0].FixtureB.UserData, new object?[] { "floor" });
    }

    private sealed class ShortFactory : IPhysxWorldEngineFactory2D
    {
        public IPhysxWorldEngine2D Create(Vector2 gravity, float fixedDeltaTime = 1f / 60f) => new Box2DWorldEngine2D(gravity, fixedDeltaTime);
    }

    [Fact]
    public void The_default_factory_overload_applies_every_setting()
    {
        var settings = new PhysxWorldSettings2D { Gravity = new Vector2(0, -3), FixedDeltaTime = 1f / 30f, VelocityIterations = 2, PositionIterations = 1, MaxSubSteps = 3 };
        IPhysxWorldEngineFactory2D factory = new ShortFactory();
        var engine = (Box2DWorldEngine2D)factory.Create(settings);
        Assert.Equal(settings, engine.Settings);
        Assert.Equal(1f / 30f, engine.FixedDeltaTime);

        // Same result as a world created with the settings directly.
        static float Fall(IPhysxWorldEngine2D w)
        {
            Ground(w);
            var c = Crate(w, new Vector2(0, 3));
            for (var i = 0; i < 40; i++) w.Step(1f / 60f);
            return c.Position.Y;
        }
        Assert.Equal(Fall(PhysxWorldEngine2D.Create(settings)), Fall(engine));
    }

    [Fact]
    public void The_2D_world_factory_is_registered_in_every_environment_mode()
    {
        Assert.Empty(typeof(WorldEngineFactory2D).GetCustomAttributes(typeof(global::Altruist.ConditionalOnConfigAttribute), false));
        Assert.NotEmpty(typeof(WorldEngineFactory2D).GetCustomAttributes(typeof(global::Altruist.ServiceAttribute), false));
    }

    [Fact]
    public void Collision_enter_carries_the_impulse_and_stay_fires_every_later_step()
    {
        var world = World();
        var ground = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Static });
        world.CreateFixture(ground, new PhysxFixtureDef2D { Shape = PhysxShape2D.Box(50, 0.5f) });
        var crate = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Dynamic, Position = new Vector2(0, 3) });
        var fixture = world.CreateFixture(crate, new PhysxFixtureDef2D { Shape = PhysxShape2D.Box(0.5f, 0.5f), Density = 1 });

        var step = 0;
        var log = new List<(string Kind, int Step, float Impulse)>();
        fixture.OnCollisionEnter += i => log.Add(("enter", step, i.Impulse));
        Action<PhysxCollisionInfo2D> stay = i => log.Add(("stay", step, i.Impulse));
        fixture.OnCollisionStay += stay;

        for (step = 1; step <= 120; step++) world.Step(1f / 60f);

        var enter = Assert.Single(log, e => e.Kind == "enter");
        // The landing stops the crate: about m * v = 1 * 10 * 38 / 60.
        Assert.InRange(enter.Impulse, 5f, 7f);
        var stays = log.Where(e => e.Kind == "stay").ToList();
        Assert.Equal(120 - enter.Step, stays.Count);
        Assert.All(stays, s => Assert.True(s.Step > enter.Step));
        // At rest the contact carries the crate's weight: m * g * dt.
        Assert.Equal(1f * 10f / 60f, stays[^1].Impulse, 2);

        fixture.OnCollisionStay -= stay;
        var count = log.Count;
        world.Step(1f / 60f);
        Assert.Equal(count, log.Count);
    }

    private sealed class Recorder(Action<IPhysxContact2D> preSolve) : IPhysxContactListener2D
    {
        public void PreSolve(IPhysxContact2D contact) => preSolve(contact);
    }

    private sealed class BeginRecorder(Action<IPhysxContact2D> begin) : IPhysxContactListener2D
    {
        public void BeginContact(IPhysxContact2D contact) => begin(contact);
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
