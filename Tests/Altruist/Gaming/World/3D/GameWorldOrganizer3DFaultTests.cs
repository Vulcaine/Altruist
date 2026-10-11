using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Numerics;
using Altruist.ThreeD.Numerics;
using Moq;

namespace Tests.Gaming.World.ThreeD;

public sealed class GameWorldOrganizer3DFaultTests
{
    private sealed class ThrowingObject3D : WorldObject3D
    {
        public int Steps;
        public ThrowingObject3D() : base(Transform3D.From(Vector3.Zero, Quaternion.Identity, Vector3.One)) { }
        public override void Step(float dt, IGameWorldManager3D world)
        {
            Steps++;
            throw new InvalidOperationException("step failed");
        }
    }

    [Fact]
    public async Task A_failing_object_step_is_logged_and_the_other_objects_still_step()
    {
        var index = new Mock<IWorldIndex3D>();
        index.SetupGet(i => i.Index).Returns(0);
        index.SetupGet(i => i.Name).Returns("test");
        index.SetupProperty(i => i.Size, new IntVector3(64, 64, 64));
        var world = new GameWorldManager3D(index.Object, physx3D: null, new WorldPartitioner3D(64, 64, 64));
        world.Initialize();
        var loader = new Mock<IWorldLoader3D>();
        loader.Setup(l => l.LoadFromIndex(It.IsAny<IWorldIndex3D>())).ReturnsAsync(world);
        var logs = new Tests.Gaming.CapturingLoggerFactory();
        var organizer = new GameWorldOrganizer3D(loader.Object, [index.Object], loggerFactory: logs);
        var a = new ThrowingObject3D();
        var b = new ThrowingObject3D();
        await world.SpawnObject(a);
        await world.SpawnObject(b);

        organizer.Step(0.02f);

        Assert.Equal(1, a.Steps);
        Assert.Equal(1, b.Steps);
        Assert.Equal(2, logs.Errors.Count(e => e is InvalidOperationException));
    }
}
