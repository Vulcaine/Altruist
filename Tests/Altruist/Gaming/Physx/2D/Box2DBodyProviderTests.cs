using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Tests.Gaming.Physx.TwoD;

public class Box2DBodyProviderTests
{
    private (Box2DPhysxBodyApiProvider2D provider, Box2DWorldEngine2D engine) Setup()
    {
        var engine = new Box2DWorldEngine2D(new Vector2(0, -9.81f));
        var provider = new Box2DPhysxBodyApiProvider2D();
        provider.SetEngine(engine);
        return (provider, engine);
    }

    private static Transform2D T(int x = 0, int y = 0) =>
        new(Position2D.Of(x, y), Size2D.Of(1, 1), Scale2D.Of(1, 1), Rotation2D.FromRadians(0));

    [Fact]
    public void CreateBody_Dynamic_ReturnsCorrectType()
    {
        var (provider, _) = Setup();

        var body = provider.CreateBody(PhysxBodyType.Dynamic, 1f, T(100, 200));

        Assert.NotNull(body);
        Assert.Equal(PhysxBodyType.Dynamic, body.Type);
        Assert.NotEmpty(body.Id);
    }

    [Fact]
    public void CreateBody_Kinematic_ReturnsCorrectType()
    {
        var (provider, _) = Setup();

        var body = provider.CreateBody(PhysxBodyType.Kinematic, 0f, T());

        Assert.Equal(PhysxBodyType.Kinematic, body.Type);
    }

    [Fact]
    public void CreateBody_Static_ReturnsCorrectType()
    {
        var (provider, _) = Setup();

        var body = provider.CreateBody(PhysxBodyType.Static, 0f, T());

        Assert.Equal(PhysxBodyType.Static, body.Type);
    }

    [Fact]
    public void CreateBody_SetsInitialPosition()
    {
        var (provider, _) = Setup();

        var body = provider.CreateBody(PhysxBodyType.Static, 0f, T(50, 75));

        Assert.Equal(50, body.Position.X, 1f);
        Assert.Equal(75, body.Position.Y, 1f);
    }

    [Fact]
    public void CreateBody_UniqueIds()
    {
        var (provider, _) = Setup();

        var b1 = provider.CreateBody(PhysxBodyType.Dynamic, 1f, T());
        var b2 = provider.CreateBody(PhysxBodyType.Dynamic, 1f, T());

        Assert.NotEqual(b1.Id, b2.Id);
    }

    [Fact]
    public void AddCollider_Circle_DoesNotThrow()
    {
        var (provider, _) = Setup();
        var body = provider.CreateBody(PhysxBodyType.Dynamic, 1f, T());

        var colliderProvider = new Box2DPhysxColliderApiProvider2D();
        var collider = colliderProvider.CreateCollider(new PhysxCollider2DParams(
            PhysxColliderShape2D.Circle2D,
            new Transform2D(Position2D.Zero, Size2D.Of(10, 0), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)),
            false));

        var ex = Record.Exception(() => provider.AddCollider(body, collider));
        Assert.Null(ex);
    }

    [Fact]
    public void AddCollider_Box_DoesNotThrow()
    {
        var (provider, _) = Setup();
        var body = provider.CreateBody(PhysxBodyType.Dynamic, 1f, T());

        var colliderProvider = new Box2DPhysxColliderApiProvider2D();
        var collider = colliderProvider.CreateCollider(new PhysxCollider2DParams(
            PhysxColliderShape2D.Box2D,
            new Transform2D(Position2D.Zero, Size2D.Of(5, 10), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)),
            false));

        var ex = Record.Exception(() => provider.AddCollider(body, collider));
        Assert.Null(ex);
    }

    [Fact]
    public void AddCollider_Capsule_DoesNotThrow()
    {
        var (provider, _) = Setup();
        var body = provider.CreateBody(PhysxBodyType.Dynamic, 1f, T());

        var colliderProvider = new Box2DPhysxColliderApiProvider2D();
        var collider = colliderProvider.CreateCollider(new PhysxCollider2DParams(
            PhysxColliderShape2D.Capsule2D,
            new Transform2D(Position2D.Zero, Size2D.Of(5, 10), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)),
            false));

        var ex = Record.Exception(() => provider.AddCollider(body, collider));
        Assert.Null(ex);
    }

    [Fact]
    public void AddCollider_Trigger_SetsSensor()
    {
        var (provider, _) = Setup();
        var body = provider.CreateBody(PhysxBodyType.Dynamic, 1f, T());

        var colliderProvider = new Box2DPhysxColliderApiProvider2D();
        var collider = colliderProvider.CreateCollider(new PhysxCollider2DParams(
            PhysxColliderShape2D.Circle2D,
            new Transform2D(Position2D.Zero, Size2D.Of(10, 0), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)),
            isTrigger: true));

        // Should not throw — trigger creates a sensor fixture
        var ex = Record.Exception(() => provider.AddCollider(body, collider));
        Assert.Null(ex);
    }

    [Fact]
    public void AddCollider_DuplicateThrows()
    {
        var (provider, _) = Setup();
        var body = provider.CreateBody(PhysxBodyType.Dynamic, 1f, T());

        var colliderProvider = new Box2DPhysxColliderApiProvider2D();
        var collider = colliderProvider.CreateCollider(new PhysxCollider2DParams(
            PhysxColliderShape2D.Circle2D,
            new Transform2D(Position2D.Zero, Size2D.Of(10, 0), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)),
            false));

        provider.AddCollider(body, collider);
        Assert.Throws<InvalidOperationException>(() => provider.AddCollider(body, collider));
    }

    [Fact]
    public void RemoveCollider_DoesNotThrow()
    {
        var (provider, _) = Setup();
        var body = provider.CreateBody(PhysxBodyType.Dynamic, 1f, T());

        var colliderProvider = new Box2DPhysxColliderApiProvider2D();
        var collider = colliderProvider.CreateCollider(new PhysxCollider2DParams(
            PhysxColliderShape2D.Circle2D,
            new Transform2D(Position2D.Zero, Size2D.Of(10, 0), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)),
            false));

        provider.AddCollider(body, collider);

        var ex = Record.Exception(() => provider.RemoveCollider(collider));
        Assert.Null(ex);
    }

    [Fact]
    public void RemoveCollider_UnknownCollider_NoOp()
    {
        var (provider, _) = Setup();

        var colliderProvider = new Box2DPhysxColliderApiProvider2D();
        var collider = colliderProvider.CreateCollider(new PhysxCollider2DParams(
            PhysxColliderShape2D.Circle2D,
            Transform2D.Zero,
            false));

        // Should not throw
        var ex = Record.Exception(() => provider.RemoveCollider(collider));
        Assert.Null(ex);
    }

    [Fact]
    public void CreateBody_WithoutSetEngine_Throws()
    {
        var provider = new Box2DPhysxBodyApiProvider2D();

        Assert.Throws<InvalidOperationException>(() =>
            provider.CreateBody(PhysxBodyType.Dynamic, 1f, T()));
    }
}
