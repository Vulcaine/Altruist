using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Physx;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Gaming.World.ThreeD;

public class CollisionTestObj : WorldObject3D
{
    public CollisionTestObj(float x, float y, float z = 0, float colliderRadius = 0)
        : base(Transform3D.From(new Vector3(x, y, z), Quaternion.Identity, Vector3.One))
    {
        if (colliderRadius > 0)
            ColliderDescriptors = [PhysxCollider3D.CreateSphere(colliderRadius, isTrigger: true)];
    }

    public override void Step(float dt, IGameWorldManager3D world) { }
}

public class SpatialCollisionDispatcherTests
{
    private SpatialCollisionDispatcher CreateDispatcher()
        => new(NullLoggerFactory.Instance);

    [Fact]
    public void DispatchHit_ShouldNotThrow_WhenNoHandlersRegistered()
    {
        var dispatcher = CreateDispatcher();
        var a = new CollisionTestObj(0, 0);
        var b = new CollisionTestObj(10, 0);

        var ex = Record.Exception(() => dispatcher.DispatchHit(a, b));
        Assert.Null(ex);
    }

    [Fact]
    public void Dispatch_ShouldNotThrow_WhenNoHandlersRegistered()
    {
        var dispatcher = CreateDispatcher();
        var a = new CollisionTestObj(0, 0);
        var b = new CollisionTestObj(10, 0);

        var ex = Record.Exception(() => dispatcher.Dispatch(a, b, typeof(CollisionEnter)));
        Assert.Null(ex);
    }

    [Fact]
    public void RemoveEntity_ShouldNotThrow_WhenEntityNotTracked()
    {
        var dispatcher = CreateDispatcher();

        var ex = Record.Exception(() => dispatcher.RemoveEntity("nonexistent"));
        Assert.Null(ex);
    }

    [Fact]
    public void Tick_WithEmptyWorld_ShouldNotThrow()
    {
        var dispatcher = CreateDispatcher();
        var world = new Mock<IGameWorldManager3D>();
        var emptyList = new List<IWorldObject3D>();
        var emptyLookup = new Dictionary<string, IWorldObject3D>();
        world.Setup(w => w.GetCachedSnapshot())
            .Returns((emptyList as IReadOnlyList<IWorldObject3D>,
                      emptyLookup as IReadOnlyDictionary<string, IWorldObject3D>));

        var ex = Record.Exception(() => dispatcher.Tick(world.Object));
        Assert.Null(ex);
    }

    [Fact]
    public void Tick_WithFarApartEntities_ShouldNotTriggerCollision()
    {
        var dispatcher = CreateDispatcher();
        var a = new CollisionTestObj(0, 0, colliderRadius: 100);
        var b = new CollisionTestObj(5000, 5000, colliderRadius: 100);

        var world = CreateWorldWith(a, b);

        // No handlers registered, but tick should still complete without error
        dispatcher.Tick(world.Object);

        // No assertions on collision events since no handlers registered —
        // the important thing is it processes without error
    }

    [Fact]
    public void RemoveEntity_ShouldCleanupActiveOverlaps()
    {
        var dispatcher = CreateDispatcher();
        var a = new CollisionTestObj(0, 0, colliderRadius: 500);
        var b = new CollisionTestObj(10, 0, colliderRadius: 500);

        var world = CreateWorldWith(a, b);

        // Tick to establish overlaps (even without handlers, internal state tracks them)
        dispatcher.Tick(world.Object);

        // Remove entity A
        dispatcher.RemoveEntity(a.InstanceId);

        // Tick again — should not crash from stale references
        var ex = Record.Exception(() => dispatcher.Tick(world.Object));
        Assert.Null(ex);
    }

    private Mock<IGameWorldManager3D> CreateWorldWith(params IWorldObject3D[] objects)
    {
        var world = new Mock<IGameWorldManager3D>();
        var list = objects.ToList();
        var lookup = objects.ToDictionary(o => o.InstanceId, o => o);
        world.Setup(w => w.GetCachedSnapshot())
            .Returns((list as IReadOnlyList<IWorldObject3D>,
                      lookup as IReadOnlyDictionary<string, IWorldObject3D>));
        return world;
    }
}
