/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming;
using Altruist.Gaming.TwoD;
using Altruist.Gaming.ThreeD;
using Altruist.Numerics;

namespace Tests.Gaming.World;

public class ZoneManager2DTests
{
    private ZoneManager2D CreateManager(int partitionWidth = 256, int partitionHeight = 256, int worldWidth = 1024, int worldHeight = 1024)
    {
        var partitioner = new TestPartitioner2D(partitionWidth, partitionHeight);
        var worldIndex = new TestWorldIndex2D(worldWidth, worldHeight);
        var partitions = partitioner.CalculatePartitions(worldIndex);
        return new ZoneManager2D(partitions);
    }

    [Fact]
    public void RegisterZone_ShouldSucceed_WhenZoneFitsInPartition()
    {
        var manager = CreateManager();
        var zone = new Zone2D("town", new IntVector2(10, 10), new IntVector2(100, 100));

        var result = manager.RegisterZone(zone);

        Assert.NotNull(result);
        Assert.Equal("town", result.Name);
    }

    [Fact]
    public void A_zone_larger_than_a_partition_registers()
    {
        var manager = CreateManager(partitionWidth: 64, partitionHeight: 64);

        var zone = manager.RegisterZone(new Zone2D("huge", new IntVector2(0, 0), new IntVector2(128, 128)));

        Assert.Same(zone, manager.FindZoneAt(100, 100));
    }

    [Fact]
    public void A_zone_crossing_a_partition_boundary_registers()
    {
        var manager = CreateManager(partitionWidth: 256, partitionHeight: 256);

        var zone = manager.RegisterZone(new Zone2D("crossing", new IntVector2(200, 200), new IntVector2(100, 100)));

        Assert.Same(zone, manager.FindZoneAt(210, 210));
        Assert.Same(zone, manager.FindZoneAt(290, 290));
    }

    [Fact]
    public void A_zone_reaching_outside_the_world_is_rejected()
    {
        var manager = CreateManager(worldWidth: 1024, worldHeight: 1024);

        Assert.Throws<ZoneValidationException>(() =>
            manager.RegisterZone(new Zone2D("edge", new IntVector2(1000, 0), new IntVector2(100, 100))));
    }

    [Fact]
    public void A_zone_without_area_is_rejected()
    {
        var manager = CreateManager();

        Assert.Throws<ZoneValidationException>(() =>
            manager.RegisterZone(new Zone2D("flat", new IntVector2(0, 0), new IntVector2(0, 100))));
    }

    [Fact]
    public void RegisterZone_ShouldThrow_WhenDuplicateName()
    {
        var manager = CreateManager();
        manager.RegisterZone(new Zone2D("town", new IntVector2(0, 0), new IntVector2(50, 50)));

        Assert.Throws<ZoneValidationException>(() =>
            manager.RegisterZone(new Zone2D("town", new IntVector2(60, 60), new IntVector2(30, 30))));
    }

    [Fact]
    public void MultipleZonesInSamePartition_ShouldSucceed()
    {
        var manager = CreateManager(partitionWidth: 256, partitionHeight: 256);

        manager.RegisterZone(new Zone2D("zone_a", new IntVector2(0, 0), new IntVector2(100, 100)));
        manager.RegisterZone(new Zone2D("zone_b", new IntVector2(100, 100), new IntVector2(100, 100)));

        Assert.Equal(2, manager.GetAllZones().Count());
    }

    [Fact]
    public void FindZoneAt_ShouldReturnCorrectZone()
    {
        var manager = CreateManager();
        manager.RegisterZone(new Zone2D("town", new IntVector2(10, 10), new IntVector2(50, 50)));
        manager.RegisterZone(new Zone2D("forest", new IntVector2(100, 100), new IntVector2(50, 50)));

        var found = manager.FindZoneAt(30, 30);
        Assert.NotNull(found);
        Assert.Equal("town", found.Name);

        var found2 = manager.FindZoneAt(120, 120);
        Assert.NotNull(found2);
        Assert.Equal("forest", found2.Name);
    }

    [Fact]
    public void FindZoneAt_ShouldReturnNull_WhenNoZoneContainsPosition()
    {
        var manager = CreateManager();
        manager.RegisterZone(new Zone2D("town", new IntVector2(10, 10), new IntVector2(50, 50)));

        var found = manager.FindZoneAt(500, 500);
        Assert.Null(found);
    }

    [Fact]
    public void FindZoneAt_ShouldSkipInactiveZones()
    {
        var manager = CreateManager();
        var zone = new Zone2D("town", new IntVector2(10, 10), new IntVector2(50, 50));
        zone.IsActive = false;
        manager.RegisterZone(zone);

        var found = manager.FindZoneAt(30, 30);
        Assert.Null(found);
    }

    [Fact]
    public void FindZonesInBounds_ShouldReturnOverlappingZones()
    {
        var manager = CreateManager();
        manager.RegisterZone(new Zone2D("a", new IntVector2(0, 0), new IntVector2(50, 50)));
        manager.RegisterZone(new Zone2D("b", new IntVector2(100, 100), new IntVector2(50, 50)));

        var found = manager.FindZonesInBounds(0, 0, 200, 200);
        Assert.Equal(2, found.Count());
    }

    [Fact]
    public void RemoveZone_ShouldRemoveRegisteredZone()
    {
        var manager = CreateManager();
        manager.RegisterZone(new Zone2D("town", new IntVector2(0, 0), new IntVector2(50, 50)));

        Assert.True(manager.RemoveZone("town"));
        Assert.Null(manager.GetZone("town"));
    }

    [Fact]
    public void GetZone_ShouldReturnNull_WhenNotRegistered()
    {
        var manager = CreateManager();
        Assert.Null(manager.GetZone("nonexistent"));
    }

    // Test helpers

    private class TestPartitioner2D : IWorldPartitioner2D
    {
        public int PartitionWidth { get; }
        public int PartitionHeight { get; }

        public TestPartitioner2D(int width, int height)
        {
            PartitionWidth = width;
            PartitionHeight = height;
        }

        public List<WorldPartition2D> CalculatePartitions(IWorldIndex2D world)
        {
            var partitions = new List<WorldPartition2D>();
            int columns = (int)Math.Ceiling((double)world.Size.X / PartitionWidth);
            int rows = (int)Math.Ceiling((double)world.Size.Y / PartitionHeight);

            for (int row = 0; row < rows; row++)
            {
                for (int col = 0; col < columns; col++)
                {
                    int x = col * PartitionWidth;
                    int y = row * PartitionHeight;
                    int w = Math.Min(PartitionWidth, world.Size.X - x);
                    int h = Math.Min(PartitionHeight, world.Size.Y - y);

                    partitions.Add(new WorldPartition2D(
                        id: $"p_{col}_{row}",
                        index: new IntVector2(col, row),
                        position: new IntVector2(x, y),
                        size: new IntVector2(w, h)));
                }
            }

            return partitions;
        }
    }

    private class TestWorldIndex2D : IWorldIndex2D
    {
        public int Index { get; set; } = 0;
        public string Name { get; set; } = "test";
        public string? DataPath { get; set; }
        public float FixedDeltaTime { get; set; } = 0.016f;
        public IntVector2 Size { get; set; }
        public System.Numerics.Vector2 Position { get; set; }
        public System.Numerics.Vector2 Gravity { get; set; }

        public TestWorldIndex2D(int width, int height)
        {
            Size = new IntVector2(width, height);
            Position = new System.Numerics.Vector2(0, 0);
            Gravity = new System.Numerics.Vector2(0, -9.81f);
        }
    }
}

public class ZoneManager3DTests
{
    /// <summary>A world of 2 x 1 x 2 partitions of the given size.</summary>
    private ZoneManager3D CreateManager(int pw = 256, int ph = 256, int pd = 256)
    {
        var partitions = new List<WorldPartitionManager3D>();
        for (int x = 0; x < 2; x++)
        {
            for (int z = 0; z < 2; z++)
            {
                partitions.Add(new(index: new IntVector3(x, 0, z), position: new IntVector3(x * pw, 0, z * pd),
                    size: new IntVector3(pw, ph, pd)));
            }
        }
        return new ZoneManager3D(partitions);
    }

    [Fact]
    public void RegisterZone_ShouldSucceed_WhenZoneFitsInPartition()
    {
        var manager = CreateManager();
        var zone = new Zone3D("dungeon", new IntVector3(10, 10, 10), new IntVector3(100, 100, 100));

        var result = manager.RegisterZone(zone);

        Assert.Equal("dungeon", result.Name);
    }

    [Fact]
    public void A_zone_crossing_a_partition_boundary_registers()
    {
        var manager = CreateManager();

        // Placed like a map in an atlas: straddles the x and z partition borders at 256.
        var zone = manager.RegisterZone(new Zone3D("deviltower", new IntVector3(200, 0, 200), new IntVector3(100, 100, 100)));

        Assert.Same(zone, manager.FindZoneAt(210, 10, 210));
        Assert.Same(zone, manager.FindZoneAt(290, 10, 290));
    }

    [Fact]
    public void A_zone_larger_than_a_partition_registers()
    {
        var manager = CreateManager(pw: 64, ph: 64, pd: 64);

        var zone = manager.RegisterZone(new Zone3D("huge", new IntVector3(0, 0, 0), new IntVector3(128, 64, 128)));

        Assert.Same(zone, manager.FindZoneAt(100, 10, 100));
    }

    [Fact]
    public void A_zone_reaching_outside_the_world_is_rejected()
    {
        var manager = CreateManager();

        Assert.Throws<ZoneValidationException>(() =>
            manager.RegisterZone(new Zone3D("edge", new IntVector3(450, 0, 0), new IntVector3(100, 100, 100))));
    }

    [Fact]
    public void A_zone_without_volume_is_rejected()
    {
        var manager = CreateManager();

        Assert.Throws<ZoneValidationException>(() =>
            manager.RegisterZone(new Zone3D("flat", new IntVector3(0, 0, 0), new IntVector3(100, 0, 100))));
    }

    [Fact]
    public void FindZoneAt_ShouldReturnCorrectZone()
    {
        var manager = CreateManager();
        manager.RegisterZone(new Zone3D("cave", new IntVector3(10, 10, 10), new IntVector3(50, 50, 50)));

        var found = manager.FindZoneAt(30, 30, 30);
        Assert.NotNull(found);
        Assert.Equal("cave", found.Name);
    }

    [Fact]
    public void FindZonesInBounds_ShouldReturnOverlapping()
    {
        var manager = CreateManager();
        manager.RegisterZone(new Zone3D("a", new IntVector3(0, 0, 0), new IntVector3(50, 50, 50)));
        manager.RegisterZone(new Zone3D("b", new IntVector3(100, 100, 100), new IntVector3(50, 50, 50)));

        var found = manager.FindZonesInBounds(0, 0, 0, 200, 200, 200);
        Assert.Equal(2, found.Count());
    }
}
