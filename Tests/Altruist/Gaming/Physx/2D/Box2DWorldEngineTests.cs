using System.Numerics;
using Altruist.Physx;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;

namespace Tests.Gaming.Physx.TwoD;

public class Box2DWorldEngineTests
{
    private static Box2DWorldEngine2D CreateEngine(float gravity = -9.81f) => new(new Vector2(0, gravity));

    private static IPhysxBody2D Body(IPhysxWorldEngine2D engine, Vector2 position, PhysxShape2D? shape = null,
        PhysxBodyType type = PhysxBodyType.Static, bool sensor = false, string? id = null)
    {
        var body = engine.CreateBody(new PhysxBodyDef2D { Type = type, Position = position, Id = id });
        engine.CreateFixture(body, new PhysxFixtureDef2D { Shape = shape ?? PhysxShape2D.Circle(1f), Density = 1f, IsTrigger = sensor });
        return body;
    }

    [Fact]
    public void Constructor_SetsFixedDeltaTime()
    {
        var engine = new Box2DWorldEngine2D(Vector2.Zero, 1f / 30f);
        Assert.Equal(1f / 30f, engine.FixedDeltaTime, 0.001f);
    }

    [Fact]
    public void Bodies_tracks_added_and_removed_bodies()
    {
        var engine = CreateEngine();
        Assert.Empty(engine.Bodies);

        var body = Body(engine, Vector2.Zero);
        Assert.Same(body, Assert.Single(engine.Bodies));

        engine.RemoveBody(body);
        Assert.Empty(engine.Bodies);
    }

    [Fact]
    public void Bodies_is_a_view_not_a_new_list_per_call()
    {
        var engine = CreateEngine();
        Body(engine, Vector2.Zero);

        Assert.Same(engine.Bodies, engine.Bodies);
    }

    [Fact]
    public void Empty_world_advances_its_fixed_step_accumulator()
    {
        var engine = new Box2DWorldEngine2D(new PhysxWorldSettings2D { Gravity = new Vector2(0, -10), FixedDeltaTime = 0.1f, MaxSubSteps = 4 });

        engine.Step(0.06f);
        var body = Body(engine, new Vector2(0, 10), type: PhysxBodyType.Dynamic);
        engine.Step(0.06f);

        // 0.12 s accumulated: one fixed step has run since the body was added.
        Assert.True(body.Position.Y < 10f);
    }

    [Fact]
    public void Step_DynamicBody_AffectedByGravity()
    {
        var engine = CreateEngine(-100f);
        var body = Body(engine, new Vector2(0, 1000), type: PhysxBodyType.Dynamic);

        for (int i = 0; i < 10; i++)
            engine.Step(1f / 60f);

        Assert.True(body.Position.Y < 1000f);
    }

    [Fact]
    public void RayCast_WithNoBodies_ReturnsEmpty()
    {
        var engine = CreateEngine();
        Assert.Empty(engine.RayCast(new PhysxRay2D(new Vector2(0, 0), new Vector2(100, 0))));
    }

    [Fact]
    public void RayCast_skips_sensors()
    {
        var engine = CreateEngine(0f);
        Body(engine, new Vector2(5, 0), sensor: true);
        var solid = Body(engine, new Vector2(10, 0));

        var hit = Assert.Single(engine.RayCast(new PhysxRay2D(Vector2.Zero, new Vector2(20, 0)), maxHits: 5));
        Assert.Same(solid, hit.Body);
        Assert.Same(solid, Assert.Single(engine.RayCast(new PhysxRay2D(Vector2.Zero, new Vector2(20, 0)))).Body);
    }

    [Fact]
    public void RayCast_reports_each_body_once_at_its_closest_hit()
    {
        var engine = CreateEngine(0f);
        var body = Body(engine, new Vector2(10, 0));
        engine.CreateFixture(body, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(1f, new Vector2(3, 0)) });

        var hit = Assert.Single(engine.RayCast(new PhysxRay2D(Vector2.Zero, new Vector2(20, 0)), maxHits: 5));
        Assert.Equal(9f, hit.Point.X, 3);
    }

    [Fact]
    public void RayCast_breaks_ties_by_body_id_whatever_the_creation_order()
    {
        foreach (var order in new[] { new[] { "b", "a" }, new[] { "a", "b" } })
        {
            var engine = CreateEngine(0f);
            foreach (var id in order)
                Body(engine, new Vector2(10, id == "a" ? 0.5f : -0.5f), PhysxShape2D.Box(1f, 0.5f), id: id);

            var hits = engine.RayCast(new PhysxRay2D(new Vector2(0, 0), new Vector2(20, 0)), maxHits: 2).ToList();
            var closest = Assert.Single(engine.RayCast(new PhysxRay2D(new Vector2(0, 0), new Vector2(20, 0))));

            Assert.Equal(new[] { "a", "b" }, hits.Select(h => h.Body.Id));
            Assert.Equal("a", closest.Body.Id);
        }
    }

    [Theory]
    [MemberData(nameof(FixtureShapes))]
    public void Fixtures_report_their_shape_kind(PhysxShape2D shape, PhysxColliderShape2D expected)
    {
        var engine = CreateEngine(0f);
        var body = engine.CreateBody(new PhysxBodyDef2D());

        var fixture = engine.CreateFixture(body, new PhysxFixtureDef2D { Shape = shape });

        Assert.Equal(expected, fixture.Shape);
    }

    public static TheoryData<PhysxShape2D, PhysxColliderShape2D> FixtureShapes() => new()
    {
        { PhysxShape2D.Circle(1f), PhysxColliderShape2D.Circle2D },
        { PhysxShape2D.Box(1f, 2f), PhysxColliderShape2D.Box2D },
        { PhysxShape2D.Polygon(new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(0, 1) }), PhysxColliderShape2D.Polygon2D },
        { PhysxShape2D.Chain(new[] { new Vector2(0, 0), new Vector2(1, 0), new Vector2(2, 1) }, loop: false), PhysxColliderShape2D.Chain2D },
        { PhysxShape2D.Edge(new Vector2(0, 0), new Vector2(1, 0)), PhysxColliderShape2D.Edge2D },
    };

    [Fact]
    public void Fixture_transform_describes_its_geometry()
    {
        var engine = CreateEngine(0f);
        var body = engine.CreateBody(new PhysxBodyDef2D());

        var circle = engine.CreateFixture(body, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(0.5f, new Vector2(1.5f, -2f)) });
        var box = engine.CreateFixture(body, new PhysxFixtureDef2D { Shape = PhysxShape2D.Box(1f, 2f, new Vector2(0.25f, 0f), 0.3f) });

        Assert.Equal(new Vector2(1.5f, -2f), circle.Transform.Position.ToVector2());
        Assert.Equal(0.5f, circle.Transform.Size.X);
        Assert.Equal(new Vector2(0.25f, 0f), box.Transform.Position.ToVector2());
        Assert.Equal(new Vector2(1f, 2f), box.Transform.Size.ToVector2());
        Assert.Equal(0.3f, box.Transform.Rotation.Radians);
        Assert.Throws<NotSupportedException>(() => circle.Transform = box.Transform);
    }

    [Fact]
    public void Dispose_ClearsAllBodies()
    {
        var engine = CreateEngine();
        Body(engine, Vector2.Zero);

        engine.Dispose();
        Assert.Empty(engine.Bodies);
    }

    [Fact]
    public void Body_Position_IsReadableAndWritable()
    {
        var engine = CreateEngine(0f);
        var body = Body(engine, new Vector2(50, 100), type: PhysxBodyType.Kinematic);

        body.Position = new Vector2(200, 300);
        Assert.Equal(new Vector2(200, 300), body.Position);
    }
}

public class WorldEngineFactory2DTests
{
    [Fact]
    public void Create_ReturnsBox2DEngine()
    {
        var factory = new WorldEngineFactory2D();
        var engine = factory.Create(new Vector2(0, -9.81f));

        Assert.IsType<Box2DWorldEngine2D>(engine);
    }

    [Fact]
    public void Create_RespectsDeltaTime()
    {
        var factory = new WorldEngineFactory2D();
        var engine = factory.Create(Vector2.Zero, 1f / 30f);

        Assert.Equal(1f / 30f, engine.FixedDeltaTime, 0.001f);
    }
}
