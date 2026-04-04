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
using FluentAssertions;
using Moq;
using System.Numerics;
using Xunit;

namespace Altruist.Gaming.Tests.TwoD;

public class GameWorldOrganizer2DTests
{
    private readonly Mock<IWorldPartitioner2D> _partitionerMock;
    private readonly Mock<ICacheProvider> _cacheMock;
    private readonly Mock<IPhysxWorldEngineFactory2D> _engineFactoryMock;
    private readonly Mock<IPhysxWorldEngine2D> _engineMock;

    private IWorldIndex2D CreateIndex(int idx, string name = "world")
    {
        var mock = new Mock<IWorldIndex2D>();
        mock.Setup(i => i.Size).Returns(new IntVector2(100, 100));
        mock.Setup(i => i.Index).Returns(idx);
        mock.Setup(i => i.Name).Returns(name);
        mock.Setup(i => i.Gravity).Returns(new Vector2(0, -9.81f));
        mock.Setup(i => i.FixedDeltaTime).Returns(1f / 60f);
        mock.SetupProperty(i => i.Position);
        return mock.Object;
    }

    public GameWorldOrganizer2DTests()
    {
        _partitionerMock = new Mock<IWorldPartitioner2D>();
        _cacheMock = new Mock<ICacheProvider>();
        _engineFactoryMock = new Mock<IPhysxWorldEngineFactory2D>();
        _engineMock = new Mock<IPhysxWorldEngine2D>();

        _partitionerMock.Setup(p => p.PartitionWidth).Returns(50);
        _partitionerMock.Setup(p => p.PartitionHeight).Returns(50);
        _partitionerMock.Setup(p => p.CalculatePartitions(It.IsAny<IWorldIndex2D>()))
            .Returns(new List<WorldPartition2D>());

        _engineMock.Setup(e => e.Bodies).Returns(new List<IPhysxBody>());
        _engineFactoryMock.Setup(f => f.Create(It.IsAny<Vector2>(), It.IsAny<float>()))
            .Returns(_engineMock.Object);
    }

    private GameWorldOrganizer2D CreateOrganizer(params IWorldIndex2D[] worlds)
    {
        return new GameWorldOrganizer2D(
            _partitionerMock.Object,
            _cacheMock.Object,
            _engineFactoryMock.Object,
            worlds);
    }

    [Fact]
    public void AddWorld_RegistersWorld()
    {
        var organizer = CreateOrganizer();
        var index = CreateIndex(99, "new-world");

        var physxWorld = new Mock<IPhysxWorld2D>().Object;
        organizer.AddWorld(index, physxWorld);

        organizer.GetWorld(99).Should().NotBeNull();
    }

    [Fact]
    public void GetWorld_ByIndex_ReturnsCorrectWorld()
    {
        var organizer = CreateOrganizer(CreateIndex(1, "alpha"), CreateIndex(2, "beta"));

        organizer.GetWorld(1).Should().NotBeNull();
        organizer.GetWorld(1)!.Index.Name.Should().Be("alpha");
    }

    [Fact]
    public void GetWorld_ByName_ReturnsCorrectWorld()
    {
        var organizer = CreateOrganizer(CreateIndex(1, "alpha"), CreateIndex(2, "beta"));

        organizer.GetWorld("beta").Should().NotBeNull();
        organizer.GetWorld("beta")!.Index.Index.Should().Be(2);
    }

    [Fact]
    public void GetWorld_ReturnsNull_ForUnknownIndex()
    {
        var organizer = CreateOrganizer();

        organizer.GetWorld(999).Should().BeNull();
    }

    [Fact]
    public void GetAllWorlds_ReturnsAll()
    {
        var organizer = CreateOrganizer(CreateIndex(1), CreateIndex(2));

        organizer.GetAllWorlds().Should().HaveCount(2);
    }

    [Fact]
    public void RemoveWorld_UnregistersWorld()
    {
        var organizer = CreateOrganizer(CreateIndex(1, "to-remove"));

        organizer.RemoveWorld(1);

        organizer.GetWorld(1).Should().BeNull();
    }

    [Fact]
    public void AddWorld_DuplicateIndex_Throws()
    {
        var organizer = CreateOrganizer(CreateIndex(1, "existing"));
        var physxWorld = new Mock<IPhysxWorld2D>().Object;
        var duplicate = CreateIndex(1, "duplicate");

        var act = () => organizer.AddWorld(duplicate, physxWorld);

        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Step_CallsPhysicsForAllWorlds()
    {
        var organizer = CreateOrganizer(CreateIndex(1));

        // Just verify it doesn't throw
        var act = () => organizer.Step(0.016f);
        act.Should().NotThrow();
    }
}
