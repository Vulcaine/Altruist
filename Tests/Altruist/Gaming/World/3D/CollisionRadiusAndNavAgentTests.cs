using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Physx;
using Altruist.Physx.ThreeD;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Gaming.World.ThreeD;

public sealed class RadiusSumObjA : CollisionTestObj
{
    public RadiusSumObjA(float x) : base(x, 0, colliderRadius: 3) { }
}

public sealed class RadiusSumObjB : CollisionTestObj
{
    public RadiusSumObjB(float x) : base(x, 0, colliderRadius: 3) { }
}

public class CollisionRadiusAndNavAgentTests
{
    [Fact]
    public void Spheres_overlap_when_the_distance_is_below_the_sum_of_their_radii()
    {
        var enters = 0;
        CollisionHandlerRegistry.Register(new CollisionHandlerRegistry.HandlerDescriptor(
            typeof(CollisionRadiusAndNavAgentTests), typeof(RadiusSumObjA), typeof(RadiusSumObjB), typeof(CollisionEnter),
            (Action<object?, object, object>)((_, _, _) => Interlocked.Increment(ref enters))));
        var a = new RadiusSumObjA(0);
        var b = new RadiusSumObjB(5);
        var world = new Mock<IGameWorldManager3D>();
        var list = new List<IWorldObject3D> { a, b };
        world.Setup(w => w.GetCachedSnapshot()).Returns((list as IReadOnlyList<IWorldObject3D>,
            list.ToDictionary(o => o.InstanceId, o => o) as IReadOnlyDictionary<string, IWorldObject3D>));

        new SpatialCollisionDispatcher(NullLoggerFactory.Instance).Tick(world.Object);

        Assert.Equal(1, enters);
    }

    [Fact]
    public void Agent_without_a_path_keeps_the_vertical_velocity()
    {
        var body = new Mock<IPhysxBody3D>();
        body.SetupProperty(b => b.LinearVelocity, new Vector3(2f, -9f, 1f));
        var agent = new NavMeshAgent(Mock.Of<INavMeshService>(), "zone", body.Object, speed: 4f);

        agent.Tick(0.1f);

        Assert.Equal(new Vector3(0f, -9f, 0f), body.Object.LinearVelocity);
    }
}
