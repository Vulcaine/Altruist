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
using System.Text.Json;
using Xunit;

namespace Altruist.Gaming.Tests.TwoD;

public class WorldLoader2DTests
{
    private readonly Mock<IPhysxWorldEngineFactory2D> _engineFactoryMock;
    private readonly Mock<IPhysxBodyApiProvider2D> _bodyApiMock;
    private readonly Mock<IPhysxColliderApiProvider2D> _colliderApiMock;
    private readonly Mock<IWorldPartitioner2D> _partitionerMock;
    private readonly Mock<IPhysxWorldEngine2D> _engineMock;
    private readonly WorldLoader2D _loader;

    public WorldLoader2DTests()
    {
        _engineFactoryMock = new Mock<IPhysxWorldEngineFactory2D>();
        _bodyApiMock = new Mock<IPhysxBodyApiProvider2D>();
        _colliderApiMock = new Mock<IPhysxColliderApiProvider2D>();
        _partitionerMock = new Mock<IWorldPartitioner2D>();
        _engineMock = new Mock<IPhysxWorldEngine2D>();

        _engineMock.Setup(e => e.Bodies).Returns(new List<IPhysxBody>());
        _engineFactoryMock
            .Setup(f => f.Create(It.IsAny<Vector2>(), It.IsAny<float>()))
            .Returns(_engineMock.Object);

        _partitionerMock.Setup(p => p.PartitionWidth).Returns(64);
        _partitionerMock.Setup(p => p.PartitionHeight).Returns(64);
        _partitionerMock
            .Setup(p => p.CalculatePartitions(It.IsAny<IWorldIndex2D>()))
            .Returns(new List<WorldPartition2D>());

        var bodyMock = new Mock<IPhysxBody2D>();
        _bodyApiMock
            .Setup(b => b.CreateBody(It.IsAny<PhysxBodyType>(), It.IsAny<float>(), It.IsAny<Transform2D>()))
            .Returns(bodyMock.Object);
        _colliderApiMock
            .Setup(c => c.CreateCollider(It.IsAny<PhysxCollider2DParams>()))
            .Returns(new Mock<IPhysxCollider2D>().Object);

        _loader = new WorldLoader2D(
            _engineFactoryMock.Object,
            _bodyApiMock.Object,
            _colliderApiMock.Object,
            _partitionerMock.Object,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    [Fact]
    public async Task LoadFromIndex_WithNoDataPath_ReturnsEmptyWorld()
    {
        var index = new WorldIndex2D(0, "test", 1f / 60f, new IntVector2(100, 100));

        var manager = await _loader.LoadFromIndex(index);

        manager.Should().NotBeNull();
        _loader.SpawnedWorldObjects.Should().BeEmpty();
    }

    [Fact]
    public async Task LoadFromIndex_WithMissingFile_ThrowsFileNotFoundException()
    {
        var index = new WorldIndex2D(0, "test", 1f / 60f, new IntVector2(100, 100),
            data: "/nonexistent/path/world.json");

        var act = async () => await _loader.LoadFromIndex(index);

        await act.Should().ThrowAsync<FileNotFoundException>();
    }

    [Fact]
    public async Task LoadFromJson_ValidSchema_CreatesWorld()
    {
        var json = """
        {
          "transform": { "position": {"X":0,"Y":0}, "rotation": 0, "size": {"X":200,"Y":200} },
          "objects": []
        }
        """;
        var index = new WorldIndex2D(0, "test", 1f / 60f, new IntVector2(200, 200));

        var manager = await _loader.LoadFromJson(index, json);

        manager.Should().NotBeNull();
    }

    [Fact]
    public async Task LoadFromJson_ObjectWithNoArchetype_SpawnsAnonymousObject()
    {
        var json = """
        {
          "transform": { "position": {"X":0,"Y":0}, "rotation": 0, "size": {"X":200,"Y":200} },
          "objects": [
            {
              "id": "obj1",
              "type": "Static",
              "position": {"X":10,"Y":10},
              "rotation": 0,
              "size": {"X":5,"Y":5},
              "colliders": [
                { "shape": "box", "size": {"X":5,"Y":5} }
              ]
            }
          ]
        }
        """;
        var index = new WorldIndex2D(0, "test", 1f / 60f, new IntVector2(200, 200));

        var manager = await _loader.LoadFromJson(index, json);

        _loader.SpawnedWorldObjects.Should().ContainSingle();
        _loader.SpawnedWorldObjects[0].Should().BeOfType<AnonymousWorldObject2D>();
    }
}
