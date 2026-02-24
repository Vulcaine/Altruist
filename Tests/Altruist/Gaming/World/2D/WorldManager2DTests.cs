/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming;
using Altruist.Gaming.TwoD;
using Altruist.Numerics;
using Altruist.Physx;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using Moq;
using System.Numerics;
using Xunit;

namespace Altruist.Gaming.Tests.TwoD;

public class WorldManager2DTests
{
    private readonly Mock<IWorldPartitioner2D> _partitionerMock;
    private readonly Mock<IPhysxWorld2D> _physxMock;
    private readonly Mock<IPhysxBodyApiProvider2D> _bodyApiMock;
    private readonly Mock<IPhysxColliderApiProvider2D> _colliderApiMock;
    private readonly Mock<IWorldIndex2D> _indexMock;
    private readonly GameWorldManager2D _manager;
    private readonly int _partWidth = 100;
    private readonly int _partHeight = 100;

    public WorldManager2DTests()
    {
        _partitionerMock = new Mock<IWorldPartitioner2D>();
        _physxMock = new Mock<IPhysxWorld2D>();
        _bodyApiMock = new Mock<IPhysxBodyApiProvider2D>();
        _colliderApiMock = new Mock<IPhysxColliderApiProvider2D>();
        _indexMock = new Mock<IWorldIndex2D>();

        _indexMock.Setup(i => i.Size).Returns(new IntVector2(200, 200));
        _indexMock.Setup(i => i.Index).Returns(0);
        _indexMock.Setup(i => i.Name).Returns("test");

        _partitionerMock.Setup(p => p.PartitionWidth).Returns(_partWidth);
        _partitionerMock.Setup(p => p.PartitionHeight).Returns(_partHeight);
        _partitionerMock.Setup(p => p.CalculatePartitions(It.IsAny<IWorldIndex2D>()))
            .Returns(new WorldPartitioner2D(_partWidth, _partHeight).CalculatePartitions(_indexMock.Object));

        _physxMock.Setup(p => p.AddBody(It.IsAny<IPhysxBody2D>()));
        _physxMock.Setup(p => p.RemoveBody(It.IsAny<IPhysxBody>()));

        var bodyMock = new Mock<IPhysxBody2D>();
        bodyMock.Setup(b => b.Position).Returns(Vector2.Zero);
        _bodyApiMock.Setup(b => b.CreateBody(It.IsAny<PhysxBodyType>(), It.IsAny<float>(), It.IsAny<Transform2D>()))
            .Returns(bodyMock.Object);
        _colliderApiMock.Setup(c => c.CreateCollider(It.IsAny<PhysxCollider2DParams>()))
            .Returns(new Mock<IPhysxCollider2D>().Object);

        _manager = new GameWorldManager2D(
            _indexMock.Object,
            _physxMock.Object,
            _partitionerMock.Object,
            null,
            _bodyApiMock.Object,
            _colliderApiMock.Object);
        _manager.Initialize();
    }

    private IWorldObject2D CreateObject(int x = 50, int y = 50, string archetype = "npc", string zoneId = "z1")
    {
        var mock = new Mock<IWorldObject2D>();
        mock.SetupProperty(o => o.InstanceId, Guid.NewGuid().ToString("N"));
        mock.SetupProperty(o => o.ObjectArchetype, archetype);
        mock.SetupProperty(o => o.ZoneId, zoneId);
        mock.SetupProperty(o => o.Body, (IPhysxBody2D?)null);
        mock.SetupProperty(o => o.Expired, false);
        mock.Setup(o => o.Transform).Returns(new Transform2D(
            Position2D.Of(x, y), Size2D.Of(2, 2), Scale2D.One, Rotation2D.Zero));
        return mock.Object;
    }

    [Fact]
    public void FindObject_ReturnsNull_WhenIdUnknown()
    {
        var result = _manager.FindObject("unknown-id");
        result.Should().BeNull();
    }

    [Fact]
    public async Task FindObject_ReturnsObject_AfterSpawnDynamic()
    {
        var obj = CreateObject();
        await _manager.SpawnDynamicObject(obj);

        var found = _manager.FindObject(obj.InstanceId);
        found.Should().NotBeNull();
        found!.InstanceId.Should().Be(obj.InstanceId);
    }

    [Fact]
    public void FindObject_ReturnsObject_AfterSpawnStatic()
    {
        var obj = CreateObject();
        _manager.SpawnStaticObject(obj);

        var found = _manager.FindObject(obj.InstanceId);
        found.Should().NotBeNull();
    }

    [Fact]
    public async Task FindAllObjects_ReturnsSubtypeOnly()
    {
        var anon = new AnonymousWorldObject2D(
            new Transform2D(Position2D.Of(50, 50), Size2D.Of(2, 2), Scale2D.One, Rotation2D.Zero),
            zoneId: "z1", archetype: "test");

        await _manager.SpawnDynamicObject(anon);

        var found = _manager.FindAllObjects<AnonymousWorldObject2D>().ToList();
        found.Should().ContainSingle();
    }

    [Fact]
    public async Task GetAllObjects_ReturnsAllSpawnedObjects()
    {
        var obj1 = CreateObject(10, 10);
        var obj2 = CreateObject(50, 50);

        await _manager.SpawnDynamicObject(obj1);
        _manager.SpawnStaticObject(obj2);

        var all = _manager.GetAllObjects().ToList();
        all.Should().HaveCountGreaterThanOrEqualTo(2);
        all.Should().Contain(o => o.InstanceId == obj1.InstanceId);
        all.Should().Contain(o => o.InstanceId == obj2.InstanceId);
    }

    [Fact]
    public async Task DestroyObject_RemovesFromCache()
    {
        var obj = CreateObject();
        await _manager.SpawnDynamicObject(obj);

        _manager.DestroyObject(obj.InstanceId);

        _manager.FindObject(obj.InstanceId).Should().BeNull();
    }

    [Fact]
    public void DestroyObject_ReturnsNull_ForUnknownId()
    {
        var result = _manager.DestroyObject("does-not-exist");
        result.Should().BeNull();
    }

    [Fact]
    public void FindPartitionsForBounds_ReturnsIntersectingPartitions()
    {
        // World is 200x200, partitions are 100x100 → 4 partitions
        // Query the center area — should intersect all 4
        var partitions = _manager.FindPartitionsForBounds(50, 50, 150, 150).ToList();

        partitions.Should().HaveCount(4);
    }

    [Fact]
    public void FindPartitionsForObject_UsesObjectTransformBounds()
    {
        var obj = CreateObject(50, 50);

        var partitions = _manager.FindPartitionsForObject(obj).ToList();

        partitions.Should().NotBeEmpty();
    }
}
