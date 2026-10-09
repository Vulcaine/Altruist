using System.Numerics;
using Altruist.Physx;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Tests.Gaming.Physx.TwoD;

public class Box2DBodyProviderTests
{
    private readonly Box2DPhysxBodyApiProvider2D _provider = new();

    private static PhysxWorld2D NewWorld(float gravity = 0f) =>
        new(new Box2DWorldEngine2D(new Vector2(0, gravity)));

    private static Transform2D T(float x = 0, float y = 0) =>
        new(Position2D.Of(x, y), Size2D.Of(1, 1), Scale2D.One, Rotation2D.Zero);

    [Theory]
    [InlineData(PhysxBodyType.Dynamic)]
    [InlineData(PhysxBodyType.Kinematic)]
    [InlineData(PhysxBodyType.Static)]
    public void CreateBody_creates_the_requested_type(PhysxBodyType type)
    {
        var body = _provider.CreateBody(NewWorld(), type, 1f, T());

        Assert.Equal(type, body.Type);
        Assert.NotEmpty(body.Id);
    }

    [Fact]
    public void CreateBody_keeps_the_exact_position()
    {
        var body = _provider.CreateBody(NewWorld(), PhysxBodyType.Static, 0f, T(50.25f, -75.5f));

        Assert.Equal(new Vector2(50.25f, -75.5f), body.Position);
    }

    [Fact]
    public void CreateBody_adds_the_body_to_the_world_it_is_given()
    {
        var first = NewWorld();
        var second = NewWorld();

        var body = _provider.CreateBody(second, PhysxBodyType.Dynamic, 1f, T());

        Assert.Empty(first.Bodies);
        Assert.Same(body, Assert.Single(second.Bodies));
    }

    [Fact]
    public void CreateBody_gives_a_dynamic_body_the_requested_mass()
    {
        var body = _provider.CreateBody(NewWorld(), PhysxBodyType.Dynamic, 7.5f, T());

        _provider.AddCollider(body, PhysxCollider2D.CreateCircle(2f));
        Assert.Equal(7.5f, body.Mass, 4);

        _provider.AddCollider(body, PhysxCollider2D.CreateRectangle(new Vector2(3, 3)));
        Assert.Equal(7.5f, body.Mass, 4);
    }

    [Fact]
    public void Attached_collider_is_recorded_and_hit_by_rays()
    {
        var world = NewWorld();
        var body = _provider.CreateBody(world, PhysxBodyType.Static, 0f, T(10, 0));
        var collider = PhysxCollider2D.CreateRectangle(new Vector2(1, 1));

        _provider.AddCollider(body, collider);

        Assert.Same(collider, Assert.Single(body.GetColliders().ToArray()));
        Assert.True(body.TryGetColliderById(collider.Id, out var found));
        Assert.Same(collider, found);
        var hit = Assert.Single(world.RayCast(new PhysxRay2D(Vector2.Zero, new Vector2(20, 0))));
        Assert.Equal(9f, hit.Point.X, 3);
    }

    [Fact]
    public void Attached_collider_tags_its_fixtures_with_its_user_data()
    {
        var world = NewWorld();
        var body = _provider.CreateBody(world, PhysxBodyType.Static, 0f, T());
        var tag = new object();
        var collider = PhysxCollider2D.CreateCircle(1f);
        collider.UserData = tag;

        _provider.AddCollider(body, collider);

        var seen = new List<object?>();
        world.Engine.RayCast(new Vector2(-5, 0), new Vector2(5, 0), new CollectTags(seen));
        Assert.Equal(new[] { tag }, seen);
    }

    [Fact]
    public void Collider_events_fire_once_per_touch_even_for_a_multi_fixture_capsule()
    {
        var world = NewWorld(gravity: -10f);
        var ground = _provider.CreateBody(world, PhysxBodyType.Static, 0f, T());
        var groundCollider = PhysxCollider2D.CreateRectangle(new Vector2(20, 0.5f));
        _provider.AddCollider(ground, groundCollider);
        var falling = _provider.CreateBody(world, PhysxBodyType.Dynamic, 1f, T(0, 2));
        var capsule = PhysxCollider2D.CreateCapsule(radius: 0.5f, halfLength: 1f);
        _provider.AddCollider(falling, capsule);

        var enters = new List<IPhysxCollider>();
        var groundEnters = new List<IPhysxCollider>();
        var stays = 0;
        capsule.OnCollisionEnter += info => enters.Add(info.OtherCollider);
        groundCollider.OnCollisionEnter += info => groundEnters.Add(info.OtherCollider);
        capsule.OnCollisionStay += _ => stays++;

        for (var i = 0; i < 120; i++) world.Step(1f / 60f);

        Assert.Same(groundCollider, Assert.Single(enters));
        Assert.Same(capsule, Assert.Single(groundEnters));
        Assert.InRange(stays, 1, 120);
    }

    [Fact]
    public void Capsule_has_round_caps()
    {
        var world = NewWorld();
        var body = _provider.CreateBody(world, PhysxBodyType.Static, 0f, T());
        _provider.AddCollider(body, PhysxCollider2D.CreateCapsule(radius: 1f, halfLength: 2f));

        // Near the corner of the bounding box (y = 0.9) a box would be hit at x = 3, the round cap much later.
        var corner = Assert.Single(world.RayCast(new PhysxRay2D(new Vector2(5, 0.9f), new Vector2(0, 0.9f))));
        Assert.True(corner.Point.X < 2.5f, $"hit at {corner.Point.X}");
        var tip = Assert.Single(world.RayCast(new PhysxRay2D(new Vector2(5, 0), new Vector2(0, 0))));
        Assert.Equal(3f, tip.Point.X, 3);
    }

    [Fact]
    public void RemoveCollider_destroys_its_fixtures()
    {
        var world = NewWorld();
        var body = _provider.CreateBody(world, PhysxBodyType.Static, 0f, T());
        var collider = PhysxCollider2D.CreateCircle(1f);
        _provider.AddCollider(body, collider);

        _provider.RemoveCollider(collider);

        Assert.Empty(body.GetColliders().ToArray());
        Assert.Null(collider.AttachedBody);
        Assert.Empty(world.RayCast(new PhysxRay2D(new Vector2(-5, 0), new Vector2(5, 0))));
    }

    [Fact]
    public void Body_RemoveCollider_destroys_an_engine_fixture()
    {
        var world = NewWorld();
        var body = world.Engine.CreateBody(new PhysxBodyDef2D());
        var fixture = world.Engine.CreateFixture(body, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(1f) });

        Assert.True(body.RemoveCollider(fixture));

        Assert.Empty(world.RayCast(new PhysxRay2D(new Vector2(-5, 0), new Vector2(5, 0))));
    }

    [Fact]
    public void Setting_the_type_changes_the_native_body()
    {
        var world = NewWorld(gravity: -10f);
        var body = _provider.CreateBody(world, PhysxBodyType.Static, 0f, T(0, 5));
        _provider.AddCollider(body, PhysxCollider2D.CreateCircle(0.5f));

        body.Type = PhysxBodyType.Dynamic;
        for (var i = 0; i < 30; i++) world.Step(1f / 60f);

        Assert.Equal(PhysxBodyType.Dynamic, body.Type);
        Assert.True(body.Position.Y < 5f);
    }

    [Fact]
    public void AddCollider_twice_throws()
    {
        var body = _provider.CreateBody(NewWorld(), PhysxBodyType.Dynamic, 1f, T());
        var collider = PhysxCollider2D.CreateCircle(1f);
        _provider.AddCollider(body, collider);

        Assert.Throws<InvalidOperationException>(() => _provider.AddCollider(body, collider));
    }

    [Fact]
    public void Attached_collider_transform_is_fixed()
    {
        var body = _provider.CreateBody(NewWorld(), PhysxBodyType.Dynamic, 1f, T());
        var collider = PhysxCollider2D.CreateCircle(1f);
        _provider.AddCollider(body, collider);

        Assert.Throws<InvalidOperationException>(() => collider.Transform = T(1, 1));
    }

    [Fact]
    public void RemoveCollider_of_a_detached_collider_is_a_no_op()
    {
        var ex = Record.Exception(() => _provider.RemoveCollider(PhysxCollider2D.CreateCircle(1f)));
        Assert.Null(ex);
    }

    private sealed class CollectTags : IPhysxRayCastCallback2D
    {
        private readonly List<object?> _tags;
        public CollectTags(List<object?> tags) => _tags = tags;

        public float OnHit(IPhysxFixture2D fixture, Vector2 point, Vector2 normal, float fraction)
        {
            _tags.Add(fixture.UserData);
            return 1f;
        }
    }
}
