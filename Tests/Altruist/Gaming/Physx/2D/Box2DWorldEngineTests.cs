using System.Numerics;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Tests.Gaming.Physx.TwoD;

public class Box2DWorldEngineTests
{
    private Box2DWorldEngine2D CreateEngine(float gravity = -9.81f)
        => new(new Vector2(0, gravity));

    private Box2DPhysxBodyApiProvider2D CreateBodyProvider(Box2DWorldEngine2D engine)
    {
        var provider = new Box2DPhysxBodyApiProvider2D();
        provider.SetEngine(engine);
        return provider;
    }

    [Fact]
    public void Constructor_SetsFixedDeltaTime()
    {
        var engine = new Box2DWorldEngine2D(Vector2.Zero, 1f / 30f);
        Assert.Equal(1f / 30f, engine.FixedDeltaTime, 0.001f);
    }

    [Fact]
    public void Bodies_EmptyByDefault()
    {
        var engine = CreateEngine();
        Assert.Empty(engine.Bodies);
    }

    [Fact]
    public void AddBody_RegistersBody()
    {
        var engine = CreateEngine();
        var provider = CreateBodyProvider(engine);

        var body = provider.CreateBody(
            Altruist.Physx.Contracts.PhysxBodyType.Dynamic, 1f,
            new Transform2D(Position2D.Of(100, 200), Size2D.Of(1, 1), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)));

        engine.AddBody(body);

        Assert.Single(engine.Bodies);
    }

    [Fact]
    public void RemoveBody_UnregistersBody()
    {
        var engine = CreateEngine();
        var provider = CreateBodyProvider(engine);

        var body = provider.CreateBody(
            Altruist.Physx.Contracts.PhysxBodyType.Dynamic, 1f,
            new Transform2D(Position2D.Of(0, 0), Size2D.Of(1, 1), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)));

        engine.AddBody(body);
        Assert.Single(engine.Bodies);

        engine.RemoveBody(body);
        Assert.Empty(engine.Bodies);
    }

    [Fact]
    public void Step_WithNoBodies_DoesNotThrow()
    {
        var engine = CreateEngine();
        var ex = Record.Exception(() => engine.Step(1f / 60f));
        Assert.Null(ex);
    }

    [Fact]
    public void Step_WithBodies_DoesNotThrow()
    {
        var engine = CreateEngine();
        var provider = CreateBodyProvider(engine);

        var body = provider.CreateBody(
            Altruist.Physx.Contracts.PhysxBodyType.Dynamic, 1f,
            new Transform2D(Position2D.Of(0, 100), Size2D.Of(1, 1), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)));
        engine.AddBody(body);

        // Need a fixture for the body to participate in physics
        var colliderProvider = new Box2DPhysxColliderApiProvider2D();
        var collider = colliderProvider.CreateCollider(new PhysxCollider2DParams(
            PhysxColliderShape2D.Circle2D,
            new Transform2D(Position2D.Zero, Size2D.Of(10, 0), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)),
            false));
        provider.AddCollider(body, collider);

        var ex = Record.Exception(() => engine.Step(1f / 60f));
        Assert.Null(ex);
    }

    [Fact]
    public void Step_DynamicBody_AffectedByGravity()
    {
        var engine = CreateEngine(-100f); // strong gravity for visible effect
        var provider = CreateBodyProvider(engine);

        var body = provider.CreateBody(
            Altruist.Physx.Contracts.PhysxBodyType.Dynamic, 1f,
            new Transform2D(Position2D.Of(0, 1000), Size2D.Of(1, 1), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)));
        engine.AddBody(body);

        // Add a fixture so Box2D processes it
        var colliderProvider = new Box2DPhysxColliderApiProvider2D();
        var collider = colliderProvider.CreateCollider(new PhysxCollider2DParams(
            PhysxColliderShape2D.Circle2D,
            new Transform2D(Position2D.Zero, Size2D.Of(5, 0), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)),
            false));
        provider.AddCollider(body, collider);

        var initialY = body.Position.Y;

        // Step multiple times
        for (int i = 0; i < 10; i++)
            engine.Step(1f / 60f);

        Assert.True(body.Position.Y < initialY, "Body should fall due to gravity");
    }

    [Fact]
    public void RayCast_WithNoBodies_ReturnsEmpty()
    {
        var engine = CreateEngine();
        var ray = new PhysxRay2D(new Vector2(0, 0), new Vector2(100, 0));

        var hits = engine.RayCast(ray);

        Assert.Empty(hits);
    }

    [Fact]
    public void Dispose_ClearsAllBodies()
    {
        var engine = CreateEngine();
        var provider = CreateBodyProvider(engine);

        var body = provider.CreateBody(
            Altruist.Physx.Contracts.PhysxBodyType.Static, 0f,
            new Transform2D(Position2D.Of(0, 0), Size2D.Of(1, 1), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)));
        engine.AddBody(body);
        Assert.Single(engine.Bodies);

        engine.Dispose();
        Assert.Empty(engine.Bodies);
    }

    [Fact]
    public void Body_Position_IsReadableAndWritable()
    {
        var engine = CreateEngine(0f);
        var provider = CreateBodyProvider(engine);

        var body = provider.CreateBody(
            Altruist.Physx.Contracts.PhysxBodyType.Kinematic, 1f,
            new Transform2D(Position2D.Of(50, 100), Size2D.Of(1, 1), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)));
        engine.AddBody(body);

        Assert.Equal(50, body.Position.X, 1f);
        Assert.Equal(100, body.Position.Y, 1f);

        body.Position = new Vector2(200, 300);
        Assert.Equal(200, body.Position.X, 1f);
        Assert.Equal(300, body.Position.Y, 1f);
    }

    [Fact]
    public void Body_LinearVelocity_IsSettable()
    {
        var engine = CreateEngine(0f);
        var provider = CreateBodyProvider(engine);

        var body = provider.CreateBody(
            Altruist.Physx.Contracts.PhysxBodyType.Dynamic, 1f,
            new Transform2D(Position2D.Of(0, 0), Size2D.Of(1, 1), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)));
        engine.AddBody(body);

        body.LinearVelocity = new Vector2(10, 5);
        Assert.Equal(10, body.LinearVelocity.X, 0.1f);
        Assert.Equal(5, body.LinearVelocity.Y, 0.1f);
    }
}

public class WorldEngineFactory2DTests
{
    [Fact]
    public void Create_ReturnsBox2DEngine()
    {
        var factory = new WorldEngineFactory2D();
        var engine = factory.Create(new Vector2(0, -9.81f));

        Assert.NotNull(engine);
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
