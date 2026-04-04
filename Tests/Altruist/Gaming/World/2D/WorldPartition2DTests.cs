/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.TwoD;
using Altruist.Numerics;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using Moq;
using Xunit;

namespace Altruist.Gaming.Tests.TwoD;

public class WorldPartition2DTests
{
    private static WorldPartition2D CreatePartition() =>
        new("p1", new IntVector2(0, 0), new IntVector2(0, 0), new IntVector2(100, 100));

    private static IWorldObject2D CreateObject(int x, int y, string archetype = "enemy", string zoneId = "zone1")
    {
        var mock = new Mock<IWorldObject2D>();
        mock.Setup(o => o.InstanceId).Returns(Guid.NewGuid().ToString("N"));
        mock.Setup(o => o.ObjectArchetype).Returns(archetype);
        mock.Setup(o => o.ZoneId).Returns(zoneId);
        mock.Setup(o => o.Transform).Returns(new Transform2D(
            Position2D.Of(x, y), Size2D.Of(1, 1), Scale2D.One, Rotation2D.Zero));
        return mock.Object;
    }

    [Fact]
    public void AddObject_IsQueryableByArchetype()
    {
        var partition = CreatePartition();
        var obj = CreateObject(10, 10);

        partition.AddObject(obj);

        var result = partition.GetObjectsByArchetype("enemy");
        result.Should().ContainSingle(o => o.InstanceId == obj.InstanceId);
    }

    [Fact]
    public void DestroyObject_ReturnsObject_AndRemovesIt()
    {
        var partition = CreatePartition();
        var obj = CreateObject(10, 10);
        partition.AddObject(obj);

        var removed = partition.DestroyObject(obj.InstanceId);

        removed.Should().NotBeNull();
        removed!.InstanceId.Should().Be(obj.InstanceId);
        partition.GetObjectsByArchetype("enemy").Should().BeEmpty();
    }

    [Fact]
    public void DestroyObject_ReturnsNull_WhenNotFound()
    {
        var partition = CreatePartition();

        var removed = partition.DestroyObject("nonexistent");

        removed.Should().BeNull();
    }

    [Fact]
    public void GetObjectsByTypeInRadius_ReturnsNearbyObjects()
    {
        var partition = CreatePartition();
        var nearby = CreateObject(10, 10);
        var farAway = CreateObject(200, 200);
        partition.AddObject(nearby);
        partition.AddObject(farAway);

        var result = partition.GetObjectsByTypeInRadius("enemy", 10, 10, 20, "zone1");

        result.Should().ContainSingle(o => o.InstanceId == nearby.InstanceId);
        result.Should().NotContain(o => o.InstanceId == farAway.InstanceId);
    }

    [Fact]
    public void GetObjectsByTypeInRoom_FiltersCorrectly()
    {
        var partition = CreatePartition();
        var inRoom = CreateObject(10, 10, "player", "room-a");
        var otherRoom = CreateObject(10, 10, "player", "room-b");
        partition.AddObject(inRoom);
        partition.AddObject(otherRoom);

        var result = partition.GetObjectsByTypeInRoom("player", "room-a");

        result.Should().ContainSingle(o => o.ZoneId == "room-a");
    }

    [Fact]
    public void GetAllObjects_GenericFilter_ReturnsCorrectType()
    {
        var partition = CreatePartition();
        var anon = new AnonymousWorldObject2D(
            new Transform2D(Position2D.Of(5, 5), Size2D.One, Scale2D.One, Rotation2D.Zero),
            zoneId: "zone1", archetype: "test");
        partition.AddObject(anon);

        var result = partition.GetAllObjects<AnonymousWorldObject2D>();

        result.Should().ContainSingle();
    }
}
