using System.Numerics;
using Altruist.Physx;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using FluentAssertions;

namespace Tests.Altruist.Physx.TwoD;

/// <summary>ContactRouter2D and the typed contact queries against a hand-written listener / contact
/// walk on two identical Box2D worlds: same contacts, same order, same normals (bits), same
/// pass-through decisions, same resulting motion.</summary>
public class ContactRouter2DTests
{
    private sealed record SurfaceTag(bool OneWay);
    private sealed record VehicleTag(int Id);
    private sealed record BallTag;

    private sealed class Arena
    {
        public required IPhysxWorldEngine2D World;
        public readonly List<IPhysxBody2D> Vehicles = new();
        public IPhysxBody2D Ball = null!;
        public IPhysxBody2D Platform = null!;
    }

    private static Arena Build()
    {
        var world = PhysxWorldEngine2D.Create(new PhysxWorldSettings2D { Gravity = new Vector2(0, -20), FixedDeltaTime = 1f / 60f });
        var a = new Arena { World = world };
        var floor = world.CreateBody(new PhysxBodyDef2D { Position = new Vector2(0, -1) });
        world.CreateFixture(floor, new PhysxFixtureDef2D { Shape = PhysxShape2D.Box(40, 1), UserData = new SurfaceTag(false) });
        var wall = world.CreateBody(new PhysxBodyDef2D { Position = new Vector2(16, 6), UserData = new SurfaceTag(false) });
        world.CreateFixture(wall, new PhysxFixtureDef2D { Shape = PhysxShape2D.Box(1, 8) });   // tag on the body
        a.Platform = world.CreateBody(new PhysxBodyDef2D { Position = new Vector2(-4, 3.5f) });
        world.CreateFixture(a.Platform, new PhysxFixtureDef2D { Shape = PhysxShape2D.Box(5, 0.25f), UserData = new SurfaceTag(true) });
        for (var i = 0; i < 6; i++)
        {
            var body = world.CreateBody(new PhysxBodyDef2D
            {
                Type = PhysxBodyType.Dynamic,
                Position = new Vector2(-10 + i * 4.1f, 0.6f + (i % 3) * 2.5f),
                Angle = 0.15f * i,
                UserData = new VehicleTag(i),
            });
            world.CreateFixture(body, new PhysxFixtureDef2D { Shape = PhysxShape2D.Box(0.9f, 0.4f), Density = 1, Friction = 0.4f, Restitution = 0.2f });
            body.LinearVelocity = new Vector2(i % 2 == 0 ? 6 : -5, 9 - i);
            a.Vehicles.Add(body);
        }
        a.Ball = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Dynamic, Position = new Vector2(-3, 1.5f), Bullet = true, UserData = new BallTag() });
        world.CreateFixture(a.Ball, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(0.8f), Density = 0.3f, Restitution = 0.6f });
        a.Ball.LinearVelocity = new Vector2(3, 14);
        return a;
    }

    private static string Bits(Vector2 v) => $"{BitConverter.SingleToInt32Bits(v.X):X8},{BitConverter.SingleToInt32Bits(v.Y):X8}";

    private static bool BallPasses(SurfaceTag s, IPhysxBody2D platform, IPhysxBody2D ball) =>
        s.OneWay && platform.GetLocalPoint(ball.Position).Y < 0.25f + 0.4f;

    private static bool CarPasses(SurfaceTag s, IPhysxBody2D platform, IPhysxBody2D car) =>
        s.OneWay && platform.GetLocalPoint(car.Position).Y < 0.25f;

    /// <summary>A pre-solve listener written the usual way: tag tests in both orders, normal flips.</summary>
    private sealed class InlineListener : IPhysxContactListener2D
    {
        public readonly List<string> Log = new();

        public void PreSolve(IPhysxContact2D contact)
        {
            var fa = contact.FixtureA;
            var fb = contact.FixtureB;
            var ua = fa.UserData ?? fa.Body.UserData;
            var ub = fb.UserData ?? fb.Body.UserData;
            if (ua is null || ub is null) return;
            if ((ua is SurfaceTag sa && ub is BallTag && BallPasses(sa, fa.Body, fb.Body)) ||
                (ub is SurfaceTag sb && ua is BallTag && BallPasses(sb, fb.Body, fa.Body)))
            {
                contact.IsEnabled = false;
                Log.Add("ball-pass");
                return;
            }
            if ((ua is SurfaceTag sc && ub is VehicleTag && CarPasses(sc, fa.Body, fb.Body)) ||
                (ub is SurfaceTag sd && ua is VehicleTag && CarPasses(sd, fb.Body, fa.Body)))
            {
                contact.IsEnabled = false;
                Log.Add("car-pass");
                return;
            }
            if (contact.PointCount == 0) return;
            var wm = contact.GetWorldManifold();
            var p = wm.Midpoint;
            var n = wm.Normal;
            switch (ua, ub)
            {
                case (SurfaceTag, VehicleTag v):
                    Log.Add($"surface:{v.Id}:{Bits(p)}:{Bits(n)}");
                    break;
                case (VehicleTag v, SurfaceTag):
                    Log.Add($"surface:{v.Id}:{Bits(p)}:{Bits(-n)}");
                    break;
                case (VehicleTag v, BallTag):
                    contact.Restitution = 0;
                    Log.Add($"ball:{v.Id}:{Bits(p)}:{Bits(n)}");
                    break;
                case (BallTag, VehicleTag v):
                    contact.Restitution = 0;
                    Log.Add($"ball:{v.Id}:{Bits(p)}:{Bits(-n)}");
                    break;
                case (SurfaceTag, BallTag):
                    Log.Add($"ball-surface:{Bits(p)}:{Bits(n)}");
                    break;
                case (BallTag, SurfaceTag):
                    Log.Add($"ball-surface:{Bits(p)}:{Bits(-n)}");
                    break;
            }
        }
    }

    private static ContactRouter2D RouterLogging(List<string> log) => new ContactRouter2D()
        .FilterPreSolve((in RoutedContact2D<SurfaceTag, BallTag> c) =>
        {
            if (!BallPasses(c.A, c.BodyA, c.BodyB)) return true;
            log.Add("ball-pass");
            return false;
        })
        .FilterPreSolve((in RoutedContact2D<SurfaceTag, VehicleTag> c) =>
        {
            if (!CarPasses(c.A, c.BodyA, c.BodyB)) return true;
            log.Add("car-pass");
            return false;
        })
        .OnPreSolve((in RoutedContact2D<SurfaceTag, VehicleTag> c) =>
        {
            var m = c.GetWorldManifold();
            log.Add($"surface:{c.B.Id}:{Bits(m.Midpoint)}:{Bits(m.Normal)}");
        })
        .OnPreSolve((in RoutedContact2D<VehicleTag, BallTag> c) =>
        {
            c.SetRestitution(0);
            var m = c.GetWorldManifold();
            log.Add($"ball:{c.A.Id}:{Bits(m.Midpoint)}:{Bits(m.Normal)}");
        })
        .OnPreSolve((in RoutedContact2D<SurfaceTag, BallTag> c) =>
        {
            var m = c.GetWorldManifold();
            log.Add($"ball-surface:{Bits(m.Midpoint)}:{Bits(m.Normal)}");
        });

    [Fact]
    public void Router_reproduces_a_hand_written_pre_solve_listener_exactly()
    {
        var a = Build();
        var b = Build();
        var inline = new InlineListener();
        var routerLog = new List<string>();
        a.World.SetContactListener(inline);
        b.World.SetContactListener(RouterLogging(routerLog));
        for (var t = 0; t < 300; t++)
        {
            a.World.Step(1f / 60f);
            b.World.Step(1f / 60f);
        }
        routerLog.Should().Equal(inline.Log);
        inline.Log.Should().Contain(s => s.StartsWith("surface:")).And.Contain(s => s.StartsWith("ball-surface:"));
        inline.Log.Should().Contain("car-pass").And.Contain("ball-pass");
        for (var i = 0; i < a.Vehicles.Count; i++)
            Bits(b.Vehicles[i].Position).Should().Be(Bits(a.Vehicles[i].Position));
        Bits(b.Ball.Position).Should().Be(Bits(a.Ball.Position));
    }

    [Fact]
    public void Router_runs_filters_then_handlers_in_registration_order_and_skips_rejected_contacts()
    {
        var log = new List<string>();
        var router = new ContactRouter2D()
            .FilterPreSolve((in RoutedContact2D<SurfaceTag, VehicleTag> _) => { log.Add("f1"); return true; })
            .OnPreSolve((in RoutedContact2D<object, VehicleTag> _) => log.Add("h-any"))
            .FilterPreSolve((in RoutedContact2D<SurfaceTag, VehicleTag> _) => { log.Add("f2"); return false; })
            .OnPreSolve((in RoutedContact2D<SurfaceTag, VehicleTag> _) => log.Add("h"));
        var a = Build();
        a.World.SetContactListener(router);
        for (var t = 0; t < 120; t++) a.World.Step(1f / 60f);
        log.Should().Contain("f1");
        log.Should().NotContain("h", "the second filter disables every surface / vehicle contact");
        for (var i = 0; i < log.Count; i++)
            if (log[i] == "f1") log[i + 1].Should().Be("f2", "filters run in registration order");
    }

    [Fact]
    public void Touching_contacts_reproduce_the_world_contact_walk_and_the_per_body_walk()
    {
        var a = Build();
        for (var t = 0; t < 200; t++)
        {
            a.World.Step(1f / 60f);

            // Inline: walk the world list, tag tests in both orders, flip to "out of the surface".
            var inline = new Dictionary<int, (Vector2 Sum, int N)>();
            foreach (var c in a.World.Contacts)
            {
                if (!c.IsTouching || !c.IsEnabled) continue;
                var ua = c.FixtureA.UserData ?? c.FixtureA.Body.UserData;
                var ub = c.FixtureB.UserData ?? c.FixtureB.Body.UserData;
                VehicleTag? vt;
                bool carIsA;
                if (ua is VehicleTag va && ub is SurfaceTag) { vt = va; carIsA = true; }
                else if (ub is VehicleTag vb && ua is SurfaceTag) { vt = vb; carIsA = false; }
                else continue;
                if (c.PointCount == 0) continue;
                var normal = c.GetWorldManifold().Normal;
                var n = carIsA ? -normal : normal;
                inline.TryGetValue(vt.Id, out var acc);
                inline[vt.Id] = (acc.Sum + n, acc.N + 1);
            }

            var routed = new Dictionary<int, (Vector2 Sum, int N)>();
            foreach (var c in a.World.TouchingContacts<SurfaceTag, VehicleTag>())
            {
                routed.TryGetValue(c.B.Id, out var acc);
                routed[c.B.Id] = (acc.Sum + c.Normal, acc.N + 1);
            }

            routed.Keys.Should().BeEquivalentTo(inline.Keys);
            foreach (var (id, acc) in inline)
            {
                routed[id].N.Should().Be(acc.N);
                Bits(routed[id].Sum).Should().Be(Bits(acc.Sum));

                // Per body (the client walks body.getContactList()): same contacts, same order, same bits.
                var perBody = Vector2.Zero;
                var count = 0;
                foreach (var c in a.World.TouchingContactsOf<SurfaceTag>(a.Vehicles[id]))
                {
                    ((VehicleTag)c.B!).Id.Should().Be(id);
                    perBody += c.Normal;
                    count++;
                }
                count.Should().Be(acc.N);
                Bits(perBody).Should().Be(Bits(acc.Sum));
            }
        }
    }

    [Fact]
    public void ContactsOf_is_the_world_order_restricted_to_the_body()
    {
        var a = Build();
        for (var t = 0; t < 120; t++) a.World.Step(1f / 60f);
        foreach (var body in a.Vehicles.Append(a.Ball))
        {
            var fromWorld = a.World.Contacts.Where(c => ReferenceEquals(c.BodyA, body) || ReferenceEquals(c.BodyB, body))
                .Select(c => (c.FixtureA, c.FixtureB)).ToList();
            var ofBody = a.World.ContactsOf(body).Select(c => (c.FixtureA, c.FixtureB)).ToList();
            ofBody.Should().Equal(fromWorld);
        }
    }

    [Fact]
    public void TryRoute_orients_pairs_and_prefers_the_unswapped_order_for_equal_types()
    {
        var a = Build();
        for (var t = 0; t < 120; t++) a.World.Step(1f / 60f);
        var seen = 0;
        foreach (var c in a.World.Contacts)
        {
            if (ContactRouter2D.TryRoute<VehicleTag, SurfaceTag>(c, out var vs))
            {
                vs.Swapped.Should().Be(ContactRouter2D.TagOf(c.FixtureA) is not VehicleTag);
                ReferenceEquals(vs.BodyA.UserData, vs.A).Should().BeTrue();
                seen++;
            }
            if (ContactRouter2D.TryRoute<VehicleTag, VehicleTag>(c, out var vv)) vv.Swapped.Should().BeFalse();
            if (ContactRouter2D.TryRoute<object, object>(c, out var any)) any.Swapped.Should().BeFalse();
        }
        seen.Should().BeGreaterThan(0);
    }
}
