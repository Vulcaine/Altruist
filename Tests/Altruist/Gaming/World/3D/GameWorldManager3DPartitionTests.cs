using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.Numerics;
using Altruist.ThreeD.Numerics;
using Moq;

namespace Tests.Gaming.World.ThreeD;

public sealed class GameWorldManager3DPartitionTests
{
    private static GameWorldManager3D CreateWorld()
    {
        var index = new Mock<IWorldIndex3D>();
        index.SetupGet(i => i.Index).Returns(0);
        index.SetupGet(i => i.Name).Returns("test");
        index.SetupProperty(i => i.Size, new IntVector3(128, 64, 64));
        var world = new GameWorldManager3D(index.Object, physx3D: null, new WorldPartitioner3D(64, 64, 64));
        world.Initialize();
        return world;
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(40, 0)]
    [InlineData(63, 0)]
    [InlineData(64, 1)]
    [InlineData(127, 1)]
    public void FindPartitionForPosition_returns_the_partition_containing_the_point(int x, int expectedColumn)
    {
        var partition = CreateWorld().FindPartitionForPosition(x, 10, 10);

        Assert.NotNull(partition);
        Assert.Equal(expectedColumn, partition!.Index.X);
    }

    [Fact]
    public async Task UpdateObjectPosition_keeps_the_object_in_the_world()
    {
        var world = CreateWorld();
        var obj = new SpawnTestObject3D();
        await world.SpawnObject(obj);

        obj.Transform = obj.Transform.WithPosition(Position3D.From(new Vector3(100, 5, 5)));
        var partitions = await world.UpdateObjectPosition(obj);

        Assert.Same(obj, world.FindObject(obj.InstanceId));
        Assert.Contains(obj, world.GetCachedSnapshot().List);
        Assert.Contains(partitions, p => p.Index.X == 1);
    }

    [Fact]
    public void Spatial_grid_remove_after_a_move_leaves_no_stale_cell_entry()
    {
        var grid = new SpatialGridIndex3D(16);
        var obj = new SpawnTestObject3D();
        grid.Add(obj);

        obj.Transform = obj.Transform.WithPosition(Position3D.From(new Vector3(100, 0, 0)));
        grid.Remove(obj.InstanceId);

        Assert.DoesNotContain(grid.Grid.Values, cell => cell.Contains(obj.InstanceId));
    }
}
