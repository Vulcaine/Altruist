using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Numerics;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;
using Moq;

namespace Tests.Gaming.World.ThreeD;

public sealed class SpawnTestObject3D : WorldObject3D
{
    public SpawnTestObject3D(bool triggerCollider = false, bool solidCollider = false)
        : base(Transform3D.From(Vector3.Zero, Quaternion.Identity, Vector3.One))
    {
        if (triggerCollider)
            ColliderDescriptors = [PhysxCollider3D.CreateSphere(1f, isTrigger: true)];

        if (solidCollider)
            ColliderDescriptors = [PhysxCollider3D.CreateSphere(1f, isTrigger: false)];
    }

    public override void Step(float dt, IGameWorldManager3D world) { }
}

public sealed class GameWorldManager3DSpawnTests
{
    [Fact]
    public async Task SpawnObject_ShouldRegisterSpatialOnly_WhenObjectHasNoPhysicsDescriptors()
    {
        var world = CreateWorld();
        var obj = new SpawnTestObject3D();

        var body = await world.SpawnObject(obj);

        Assert.Null(body);
        Assert.Null(obj.Body);
        Assert.Same(obj, world.FindObject(obj.InstanceId));
    }

    [Fact]
    public async Task SpawnObject_ShouldRegisterSpatialOnly_WhenObjectHasOnlyTriggerColliders()
    {
        var world = CreateWorld();
        var obj = new SpawnTestObject3D(triggerCollider: true);

        var body = await world.SpawnObject(obj);

        Assert.Null(body);
        Assert.Null(obj.Body);
        Assert.Same(obj, world.FindObject(obj.InstanceId));
    }

    [Fact]
    public async Task SpawnObject_ShouldCreateBody_WhenObjectHasSolidCollider()
    {
        var world = CreateWorld();
        var obj = new SpawnTestObject3D(solidCollider: true);

        var body = await world.SpawnObject(obj);

        Assert.NotNull(body);
        Assert.Same(body, obj.Body);
        Assert.Same(obj, world.FindObject(obj.InstanceId));
    }

    private static GameWorldManager3D CreateWorld()
    {
        var index = new Mock<IWorldIndex3D>();
        index.SetupGet(i => i.Index).Returns(0);
        index.SetupGet(i => i.Name).Returns("test");
        index.SetupProperty(i => i.Size, new IntVector3(64, 64, 64));

        return new GameWorldManager3D(
            index.Object,
            physx3D: null,
            new WorldPartitioner3D(64, 64, 64));
    }
}
