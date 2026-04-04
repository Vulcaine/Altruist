using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;

namespace Tests.Gaming.Physx.TwoD;

public class Box2DColliderProviderTests
{
    private readonly Box2DPhysxColliderApiProvider2D _provider = new();

    [Fact]
    public void CreateCollider_Circle_ReturnsCorrectShape()
    {
        var p = new PhysxCollider2DParams(
            PhysxColliderShape2D.Circle2D,
            new Transform2D(Position2D.Of(10, 20), Size2D.Of(50, 0), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)),
            isTrigger: false);

        var collider = _provider.CreateCollider(p);

        Assert.Equal(PhysxColliderShape2D.Circle2D, collider.Shape);
        Assert.Equal(10, collider.Transform.Position.X);
        Assert.Equal(20, collider.Transform.Position.Y);
        Assert.False(collider.IsTrigger);
    }

    [Fact]
    public void CreateCollider_Box_ReturnsCorrectShape()
    {
        var p = new PhysxCollider2DParams(
            PhysxColliderShape2D.Box2D,
            new Transform2D(Position2D.Zero, Size2D.Of(100, 50), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)),
            isTrigger: true);

        var collider = _provider.CreateCollider(p);

        Assert.Equal(PhysxColliderShape2D.Box2D, collider.Shape);
        Assert.True(collider.IsTrigger);
    }

    [Fact]
    public void CreateCollider_Capsule_ReturnsCorrectShape()
    {
        var p = new PhysxCollider2DParams(
            PhysxColliderShape2D.Capsule2D,
            new Transform2D(Position2D.Zero, Size2D.Of(25, 50), Scale2D.Of(1, 1), Rotation2D.FromRadians(0)),
            isTrigger: false);

        var collider = _provider.CreateCollider(p);

        Assert.Equal(PhysxColliderShape2D.Capsule2D, collider.Shape);
    }

    [Fact]
    public void CreateCollider_HasUniqueId()
    {
        var p = new PhysxCollider2DParams(
            PhysxColliderShape2D.Circle2D,
            Transform2D.Zero,
            isTrigger: false);

        var c1 = _provider.CreateCollider(p);
        var c2 = _provider.CreateCollider(p);

        Assert.NotEqual(c1.Id, c2.Id);
    }

    [Fact]
    public void CreateCollider_IsTriggerSettable()
    {
        var p = new PhysxCollider2DParams(
            PhysxColliderShape2D.Circle2D,
            Transform2D.Zero,
            isTrigger: false);

        var collider = _provider.CreateCollider(p);
        Assert.False(collider.IsTrigger);

        collider.IsTrigger = true;
        Assert.True(collider.IsTrigger);
    }

    [Fact]
    public void CreateCollider_UserDataSettable()
    {
        var p = new PhysxCollider2DParams(
            PhysxColliderShape2D.Box2D,
            Transform2D.Zero,
            isTrigger: false);

        var collider = _provider.CreateCollider(p);
        Assert.Null(collider.UserData);

        collider.UserData = "test_data";
        Assert.Equal("test_data", collider.UserData);
    }

    [Fact]
    public void CreateCollider_PolygonShape_HasNullVerticesByDefault()
    {
        var p = new PhysxCollider2DParams(
            PhysxColliderShape2D.Polygon2D,
            Transform2D.Zero,
            isTrigger: false);

        var collider = _provider.CreateCollider(p);

        Assert.Equal(PhysxColliderShape2D.Polygon2D, collider.Shape);
        Assert.Null(collider.Vertices);
    }
}
