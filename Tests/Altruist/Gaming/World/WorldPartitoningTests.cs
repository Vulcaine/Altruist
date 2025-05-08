/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

namespace Altruist.Gaming;

using System.Numerics;
using FluentAssertions;
using Xunit;

public class TestWorldIndex : WorldIndex
{
    public TestWorldIndex(int index, int width, int height) : base(index, new Vector2(width, height))
    {
    }
}

public class WorldPartitionerTests
{
    [Fact]
    public void Constructor_ShouldSetPartitionDimensions()
    {
        // Arrange
        int partitionWidth = 50;
        int partitionHeight = 50;

        // Act
        var partitioner = new WorldPartitioner(partitionWidth, partitionHeight);

        // Assert
        partitioner.PartitionWidth.Should().Be(partitionWidth);
        partitioner.PartitionHeight.Should().Be(partitionHeight);
    }

    [Fact]
    public void CalculatePartitions_ShouldReturnCorrectNumberOfPartitions()
    {
        // Arrange
        var worldIndex = new TestWorldIndex(1, 200, 200); // World size: 200x200
        var partitioner = new WorldPartitioner(50, 50); // Partition size: 50x50

        // Act
        var partitions = partitioner.CalculatePartitions(worldIndex);

        // Assert
        partitions.Should().HaveCount(16); // 4 rows x 4 columns = 16 partitions
    }

    [Fact]
    public void CalculatePartitions_ShouldNotExceedWorldSize()
    {
        // Arrange
        var worldIndex = new TestWorldIndex(1, 200, 200); // World size: 200x200
        var partitioner = new WorldPartitioner(50, 50); // Partition size: 50x50

        // Act
        var partitions = partitioner.CalculatePartitions(worldIndex);

        // Assert
        foreach (var partition in partitions)
        {
            partition.Position.X.Should().BeLessThanOrEqualTo(worldIndex.Width);
            partition.Position.Y.Should().BeLessThanOrEqualTo(worldIndex.Height);

            partition.Size.X.Should().BeLessThanOrEqualTo(partitioner.PartitionWidth);
            partition.Size.Y.Should().BeLessThanOrEqualTo(partitioner.PartitionHeight);
        }
    }

    [Fact]
    public void CalculatePartitions_ShouldHandleRemainderPartitions()
    {
        // Arrange
        var worldIndex = new TestWorldIndex(1, 120, 120); // World size: 120x120
        var partitioner = new WorldPartitioner(50, 50); // Partition size: 50x50

        // Act
        var partitions = partitioner.CalculatePartitions(worldIndex);

        // Assert
        partitions.Should().HaveCount(9); // 3 rows x 3 columns = 9 partitions

        // Ensure last partition size is handled properly (not exceeding the world boundary)
        var lastPartition = partitions[partitions.Count - 1];
        lastPartition.Size.X.Should().Be(20); // 120 - 2*50 = 20 (width for last partition)
        lastPartition.Size.Y.Should().Be(20); // 120 - 2*50 = 20 (height for last partition)
    }

    [Fact]
    public void CalculatePartitions_ShouldReturnCorrectPartitionPositions()
    {
        // Arrange
        var worldIndex = new TestWorldIndex(1, 100, 100); // World size: 100x100
        var partitioner = new WorldPartitioner(50, 50); // Partition size: 50x50

        // Act
        var partitions = partitioner.CalculatePartitions(worldIndex);

        // Assert
        partitions[0].Position.Should().BeEquivalentTo(new IntVector2(0, 0));
        partitions[1].Position.Should().BeEquivalentTo(new IntVector2(50, 0));
        partitions[2].Position.Should().BeEquivalentTo(new IntVector2(0, 50));
        partitions[3].Position.Should().BeEquivalentTo(new IntVector2(50, 50));
    }
}
