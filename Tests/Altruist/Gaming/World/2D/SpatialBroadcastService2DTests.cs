/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming;
using Altruist.Gaming.TwoD;
using Altruist.Numerics;
using Altruist.TwoD.Numerics;
using FluentAssertions;
using Moq;
using Xunit;

namespace Altruist.Gaming.Tests.TwoD;

public class SpatialBroadcastService2DTests
{
    private readonly Mock<IGameWorldOrganizer2D> _organizerMock;
    private readonly Mock<IAltruistRouter> _routerMock;
    private readonly Mock<ISocketManager> _socketManagerMock;
    private readonly SpatialBroadcastService2D _service;

    public SpatialBroadcastService2DTests()
    {
        _organizerMock = new Mock<IGameWorldOrganizer2D>();
        _routerMock = new Mock<IAltruistRouter>();
        _socketManagerMock = new Mock<ISocketManager>();

        _service = new SpatialBroadcastService2D(
            _organizerMock.Object,
            _routerMock.Object,
            _socketManagerMock.Object);
    }

    [Fact]
    public async Task SpatialBroadcast_DoesNotThrow_WhenWorldNotFound()
    {
        _organizerMock.Setup(o => o.GetWorld(It.IsAny<int>())).Returns((IGameWorldManager2D?)null);

        var packet = new Mock<IPacketBase>().Object;
        var act = async () => await _service.SpatialBroadcast<IWorldObject2D>(99, new IntVector2(0, 0), packet);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task SmartSpatialBroadcast_BelowThreshold_BroadcastsToRoom()
    {
        var roomMock = new RoomPacket
        {
            Id = "room-1",
            ConnectionIds = new HashSet<string> { "c1", "c2" }
        };
        _socketManagerMock
            .Setup(s => s.FindRoomForClientAsync(It.IsAny<string>()))
            .ReturnsAsync(roomMock);

        var roomSenderMock = new Mock<RoomSender>(
            new Mock<IConnectionStore>().Object,
            new Mock<ICodec>().Object,
            new Mock<ClientSender>(
                new Mock<IConnectionStore>().Object,
                new Mock<ICodec>().Object).Object);
        roomSenderMock
            .Setup(r => r.SendAsync(It.IsAny<string>(), It.IsAny<IPacketBase>()))
            .Returns(Task.CompletedTask);

        _routerMock.Setup(r => r.Room).Returns(roomSenderMock.Object);

        var packet = new Mock<IPacketBase>().Object;
        await _service.SmartSpatialBroadcast<IWorldObject2D>("client1", 0, new IntVector2(0, 0), packet, threshold: 100);

        roomSenderMock.Verify(r => r.SendAsync("room-1", It.IsAny<IPacketBase>()), Times.Once);
    }

    [Fact]
    public async Task SmartSpatialBroadcast_AboveThreshold_FallsBackToSpatialBroadcast()
    {
        var bigRoom = new RoomPacket
        {
            Id = "big-room",
            ConnectionIds = Enumerable.Range(0, 200).Select(i => $"c{i}").ToHashSet()
        };
        _socketManagerMock
            .Setup(s => s.FindRoomForClientAsync(It.IsAny<string>()))
            .ReturnsAsync(bigRoom);

        _organizerMock.Setup(o => o.GetWorld(It.IsAny<int>())).Returns((IGameWorldManager2D?)null);

        var packet = new Mock<IPacketBase>().Object;
        // Above threshold → falls back to SpatialBroadcast (world not found → no-op, but should not throw)
        var act = async () => await _service.SmartSpatialBroadcast<IWorldObject2D>(
            "client1", 0, new IntVector2(0, 0), packet, threshold: 100);

        await act.Should().NotThrowAsync();
    }
}
