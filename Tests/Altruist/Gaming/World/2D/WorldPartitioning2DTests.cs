/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming;
using Altruist.Gaming.TwoD;
using Altruist.Numerics;
using FluentAssertions;
using Moq;
using System.Numerics;
using Xunit;

namespace Altruist.Gaming.Tests.TwoD;

public class WorldPartitioning2DTests
{
    private static IWorldIndex2D CreateIndex(int w, int h)
    {
        var mock = new Mock<IWorldIndex2D>();
        mock.Setup(i => i.Size).Returns(new IntVector2(w, h));
        mock.Setup(i => i.Index).Returns(0);
        mock.Setup(i => i.Name).Returns("test");
        mock.Setup(i => i.Gravity).Returns(new Vector2(0, -9.81f));
        mock.Setup(i => i.FixedDeltaTime).Returns(1f / 60f);
        mock.SetupProperty(i => i.Position);
        return mock.Object;
    }

    [Fact]
    public void CalculatePartitions_ReturnsCorrectCount()
    {
        var partitioner = new WorldPartitioner2D(50, 50);
        var index = CreateIndex(200, 200);

        var partitions = partitioner.CalculatePartitions(index);

        // 200/50 = 4 columns × 4 rows = 16
        partitions.Should().HaveCount(16);
    }

    [Fact]
    public void CalculatePartitions_NeverExceedsWorldSize()
    {
        var partitioner = new WorldPartitioner2D(50, 50);
        var index = CreateIndex(200, 200);

        var partitions = partitioner.CalculatePartitions(index);

        foreach (var p in partitions)
        {
            (p.Position.X + p.Size.X).Should().BeLessThanOrEqualTo(200);
            (p.Position.Y + p.Size.Y).Should().BeLessThanOrEqualTo(200);
        }
    }

    [Fact]
    public void CalculatePartitions_HandlesRemainderPartitions()
    {
        var partitioner = new WorldPartitioner2D(50, 50);
        var index = CreateIndex(120, 120);

        var partitions = partitioner.CalculatePartitions(index);

        // ceil(120/50)=3 cols × 3 rows = 9
        partitions.Should().HaveCount(9);

        // The last partition in a row should be <= 20 wide
        var lastInFirstRow = partitions.Where(p => p.Index.Y == 0).MaxBy(p => p.Index.X)!;
        lastInFirstRow.Size.X.Should().BeLessThanOrEqualTo(20);
    }

    [Fact]
    public void CalculatePartitions_ReturnsCorrectPositions()
    {
        var partitioner = new WorldPartitioner2D(64, 64);
        var index = CreateIndex(128, 128);

        var partitions = partitioner.CalculatePartitions(index);

        partitions.Should().Contain(p => p.Position.X == 0 && p.Position.Y == 0);
        partitions.Should().Contain(p => p.Position.X == 64 && p.Position.Y == 0);
        partitions.Should().Contain(p => p.Position.X == 0 && p.Position.Y == 64);
        partitions.Should().Contain(p => p.Position.X == 64 && p.Position.Y == 64);
    }
}
