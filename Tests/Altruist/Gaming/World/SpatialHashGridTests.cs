using System.Numerics;
using Altruist.Gaming;
using Altruist.Gaming.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Tests.Gaming.World;

public class GridTestObj : WorldObject3D
{
    public GridTestObj(float x, float y)
        : base(Transform3D.From(new Vector3(x, y, 0), Quaternion.Identity, Vector3.One))
    { }

    public override void Step(float dt, IGameWorldManager3D world) { }
}

public class SpatialHashGridTests
{
    private SpatialHashGrid CreateGrid(float cellSize = 500f) => new(cellSize);

    private List<ITypelessWorldObject> CreateObjects(params (float x, float y)[] positions)
    {
        return positions.Select(p => (ITypelessWorldObject)new GridTestObj(p.x, p.y)).ToList();
    }

    [Fact]
    public void Build_WithEmptyList_DoesNotThrow()
    {
        var grid = CreateGrid();
        var ex = Record.Exception(() => grid.Build(new List<ITypelessWorldObject>()));
        Assert.Null(ex);
    }

    [Fact]
    public void QueryRadius_AfterBuild_FindsNearbyObjects()
    {
        var grid = CreateGrid(500f);
        var objects = CreateObjects((100, 100), (200, 200), (5000, 5000));
        grid.Build(objects);

        var results = new List<int>();
        grid.QueryRadius(150, 150, 500f, results);

        // Objects at (100,100) and (200,200) are within 500 of (150,150)
        Assert.Contains(0, results);
        Assert.Contains(1, results);
    }

    [Fact]
    public void QueryRadius_DoesNotReturnDistantObjects()
    {
        var grid = CreateGrid(500f);
        var objects = CreateObjects((0, 0), (5000, 5000));
        grid.Build(objects);

        var results = new List<int>();
        grid.QueryRadius(0, 0, 100f, results);

        // Only the nearby object should be in the candidate list
        // (the grid returns candidates; exact distance is caller's job)
        Assert.DoesNotContain(1, results);
    }

    [Fact]
    public void QueryRadius_ReusesBuffer()
    {
        var grid = CreateGrid(500f);
        var objects = CreateObjects((100, 100), (200, 200));
        grid.Build(objects);

        var results = new List<int>();
        grid.QueryRadius(150, 150, 1000f, results);
        Assert.True(results.Count > 0);

        // Second query clears and reuses same buffer
        grid.QueryRadius(9999, 9999, 100f, results);
        Assert.Empty(results);
    }

    [Fact]
    public void Build_CanBeCalledMultipleTimes()
    {
        var grid = CreateGrid(500f);
        var objects1 = CreateObjects((100, 100));
        var objects2 = CreateObjects((5000, 5000));

        grid.Build(objects1);
        var results = new List<int>();
        grid.QueryRadius(100, 100, 200f, results);
        Assert.NotEmpty(results);

        // Rebuild with different objects
        grid.Build(objects2);
        grid.QueryRadius(100, 100, 200f, results);
        Assert.Empty(results); // Old objects gone

        grid.QueryRadius(5000, 5000, 200f, results);
        Assert.NotEmpty(results); // New objects found
    }

    [Fact]
    public void Build_ManyObjects_HandlesCorrectly()
    {
        var grid = CreateGrid(500f);
        var rng = new Random(42);
        var positions = Enumerable.Range(0, 1000)
            .Select(_ => ((float)rng.Next(0, 10000), (float)rng.Next(0, 10000)))
            .ToArray();
        var objects = CreateObjects(positions);
        grid.Build(objects);

        var results = new List<int>();
        grid.QueryRadius(5000, 5000, 500f, results);

        // Should find some objects near center
        Assert.True(results.Count > 0);
        Assert.True(results.Count < 1000); // Should NOT return all objects
    }

    [Fact]
    public void Build_TypedOverload_WorksWithIWorldObject3D()
    {
        var grid = CreateGrid(500f);
        var objects = new List<IWorldObject3D>
        {
            new GridTestObj(100, 100),
            new GridTestObj(5000, 5000),
        };
        grid.Build(objects);

        var results = new List<int>();
        grid.QueryRadius(100, 100, 200f, results);
        Assert.Contains(0, results);
    }

    [Fact]
    public void SmallCellSize_MorePreciseFiltering()
    {
        var grid = CreateGrid(100f); // Small cells
        var objects = CreateObjects((0, 0), (50, 0), (1000, 0));
        grid.Build(objects);

        var results = new List<int>();
        grid.QueryRadius(0, 0, 100f, results);

        Assert.Contains(0, results);
        Assert.Contains(1, results);
        Assert.DoesNotContain(2, results);
    }
}
