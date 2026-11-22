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

    [Fact]
    public async Task Worlds_sharing_an_allocator_never_hand_out_the_same_virtual_id()
    {
        var allocator = new VirtualIdAllocator();
        var preAllocated = allocator.Allocate();
        var first = new SpawnTestObject3D();
        var second = new SpawnTestObject3D();

        await CreateWorld(allocator).SpawnObject(first);
        await CreateWorld(allocator).SpawnObject(second);

        Assert.Equal(3, new[] { preAllocated, first.VirtualId, second.VirtualId }.Distinct().Count());
    }

    [Fact]
    public async Task An_object_spawned_with_a_virtual_id_keeps_it()
    {
        var obj = new SpawnTestObject3D { VirtualId = 42 };

        await CreateWorld().SpawnObject(obj);

        Assert.Equal(42u, obj.VirtualId);
    }

    private static GameWorldManager3D CreateWorld() => CreateWorld(new VirtualIdAllocator());

    private static GameWorldManager3D CreateWorld(IVirtualIdAllocator allocator)
    {
        var index = new Mock<IWorldIndex3D>();
        index.SetupGet(i => i.Index).Returns(0);
        index.SetupGet(i => i.Name).Returns("test");
        index.SetupProperty(i => i.Size, new IntVector3(64, 64, 64));

        return new GameWorldManager3D(
            index.Object,
            physx3D: null,
            new WorldPartitioner3D(64, 64, 64),
            allocator);
    }
}
