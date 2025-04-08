namespace Altruist.Gaming;

using Xunit;
using FluentAssertions;
using Moq;
using System.Collections.Generic;
using System.Threading.Tasks;

public class GameWorldManagerTests
{
    private readonly Mock<IWorldPartitioner> _partitionerMock;
    private readonly Mock<ICacheProvider> _cacheMock;
    private readonly GameWorldManager _manager;
    private readonly WorldIndex _worldIndex;

    private (int Width, int Height) _partitionSize = (50, 50);

    private readonly List<WorldPartition> _mockPartitions;

    public GameWorldManagerTests()
    {
        _partitionerMock = new Mock<IWorldPartitioner>();
        _cacheMock = new Mock<ICacheProvider>();

        _worldIndex = new WorldIndex(1, 100, 100);

        _mockPartitions = new List<WorldPartition>
        {
            CreatePartition(0, 0),
            CreatePartition(1, 0),
            CreatePartition(0, 1),
            CreatePartition(1, 1)
        };

        _partitionerMock.Setup(p => p.PartitionWidth).Returns(_partitionSize.Width);
        _partitionerMock.Setup(p => p.PartitionHeight).Returns(_partitionSize.Height);
        _partitionerMock.Setup(p => p.CalculatePartitions(It.IsAny<WorldIndex>())).Returns(_mockPartitions);

        _manager = new GameWorldManager(_worldIndex, _partitionerMock.Object, _cacheMock.Object);
    }

    private WorldPartition CreatePartition(int x, int y)
    {
        var mock = new Mock<WorldPartition>(
            $"{x}-{y}",
            new IntVector2(x, y),
            new IntVector2((x + 1) * _partitionSize.Width, (y + 1) * _partitionSize.Height),
            new IntVector2((x + 1) * _partitionSize.Width + x / 2, (y + 1) * _partitionSize.Height + y / 2));
        return mock.Object;
    }

    [Fact]
    public void Initialize_ShouldPopulatePartitions()
    {
        _manager.Initialize();

        var partition = _manager.FindPartitionForPosition(25, 25);

        partition.Should().NotBeNull();
    }

    [Fact]
    public async Task SaveAsync_ShouldCallCacheSave()
    {
        _manager.Initialize();
        await _manager.SaveAsync();

        foreach (var partition in _mockPartitions)
        {
            // Initialize also calling the save once
            _cacheMock.Verify(c => c.SaveAsync(partition.Id, partition), Times.Exactly(2));
        }
    }

    [Fact]
    public void AddStaticObject_ShouldAddToCorrectPartition()
    {
        _manager.Initialize();

        Console.WriteLine(_mockPartitions.Count);

        var objectType = new WorldObjectTypeKey("TestType");
        var obj = new ObjectMetadata
        {
            InstanceId = "obj-1",
            Position = new IntVector2(25, 25)
        };

        var partition = _manager.AddStaticObject(objectType, obj);
        partition.Should().NotBeNull();
    }

    [Fact]
    public void UpdateObjectPosition_ShouldRemoveAndAddObject()
    {
        _manager.Initialize();

        var objectType = new WorldObjectTypeKey("TestType");
        var metadata = new ObjectMetadata
        {
            InstanceId = "move-1",
            Position = new IntVector2(75, 75)
        };

        var mockPartition = new Mock<WorldPartition>("1-1",
            new IntVector2(1, 1),
            new IntVector2(_partitionSize.Width,
            _partitionSize.Height),
            new IntVector2(_partitionSize.Width, _partitionSize.Height));
        mockPartition.Setup(p => p.DestroyObject(objectType, metadata.InstanceId)).Returns(metadata);

        _mockPartitions.Add(mockPartition.Object);

        var affectedPartitions = _manager.UpdateObjectPosition(objectType, metadata, 10).ToList();

        affectedPartitions.Should().NotBeEmpty();
    }

    [Fact]
    public void GetNearbyObjectsInRoom_ShouldReturnObjectsWithinRadius()
    {
        _manager.Initialize();

        var objectType = new WorldObjectTypeKey("NPC");
        var roomId = "r1";

        var metadata = new ObjectMetadata
        {
            InstanceId = "npc-1",
            Position = new IntVector2(30, 30),
            RoomId = roomId,
            Type = objectType
        };

        var partition = _mockPartitions[0];
        var mock = Mock.Get(partition);

        mock.Setup(p => p.GetObjectsByTypeInRadius(objectType, 30, 30, 20, roomId)).Returns([metadata]);
        var result = _manager.GetNearbyObjectsInRoom(objectType, 30, 30, 20, roomId);
        result.Should().ContainSingle(o => o.InstanceId == "npc-1");
    }

    [Fact]
    public void FindPartitionsForPosition_ShouldReturnIntersectingPartitions()
    {
        _manager.Initialize();

        var results = _manager.FindPartitionsForPosition(30, 30, 10);

        results.Should().OnlyContain(p =>
            p.Position.X <= 40 &&
            p.Position.X + p.Size.X >= 20 &&
            p.Position.Y <= 40 &&
            p.Position.Y + p.Size.Y >= 20
        );
    }
}
