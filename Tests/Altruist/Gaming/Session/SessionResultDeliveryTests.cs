using Altruist;
using Altruist.Gaming;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace Tests.Gaming.Session;

public sealed class SessionResultDeliveryTests
{
    [Fact]
    public async Task Failed_join_result_is_delivered_with_its_code_and_reason()
    {
        var (portal, sent) = PortalReturning(ResultPacket.Failed(TransportCode.NotFound, "No such room"));

        await portal.JoinGameAsync(new JoinGamePacket("p1"), "client-1");

        var failed = Assert.IsType<FailedPacket>(Assert.Single(sent));
        Assert.Equal(TransportCode.NotFound, failed.Code);
        Assert.Equal("No such room", failed.Reason);
    }

    [Fact]
    public async Task Payload_less_success_is_delivered_with_its_code()
    {
        var (portal, sent) = PortalReturning(ResultPacket.Success(TransportCode.Created));

        await portal.JoinGameAsync(new JoinGamePacket("p1"), "client-1");

        var success = Assert.IsType<SuccessPacket>(Assert.Single(sent));
        Assert.Equal(TransportCode.Created, success.Code);
    }

    [Fact]
    public async Task Success_with_payload_delivers_the_payload()
    {
        var (portal, sent) = PortalReturning(ResultPacket.Success(TransportCode.Ok, "joined"));

        await portal.JoinGameAsync(new JoinGamePacket("p1"), "client-1");

        var text = Assert.IsType<TextPacket>(Assert.Single(sent));
        Assert.Equal("joined", text.Text);
    }

    [Fact]
    public async Task Successful_join_reports_ok()
    {
        var sockets = new Mock<ISocketManager>();
        sockets.Setup(s => s.FindAvailableRoomAsync()).ReturnsAsync(new RoomPacket("room-1"));
        var service = new GameSessionService(sockets.Object, NullLoggerFactory.Instance);

        var result = await service.JoinGameAsync(new JoinGamePacket("p1"), "client-1");

        Assert.Equal(TransportCode.Ok, Assert.IsType<SuccessPacket>(result).Code);
    }

    [Fact]
    public void Join_request_constructors_default_to_no_room()
    {
        Assert.Null(new JoinGamePacket().RoomId);
        Assert.Null(new JoinGamePacket("p1").RoomId);
    }

    [Fact]
    public void Room_capacity_is_spelled_correctly_and_keeps_its_wire_name()
    {
        var room = new RoomPacket("room-1", maxCapacity: 2);

        var json = System.Text.Json.JsonSerializer.Serialize(room);

        Assert.Equal(2u, room.MaxCapacity);
        Assert.Contains("\"maxCapacity\":2", json);
        Assert.DoesNotContain("MaxCapactiy", json);
    }

    private static (JoinPortal Portal, List<IPacketBase> Sent) PortalReturning(IResultPacket result)
    {
        var sender = new RecordingClientSender();
        var router = new Mock<IAltruistRouter>();
        router.SetupGet(r => r.Client).Returns(sender);
        var sessions = new Mock<IGameSessionService>();
        sessions.Setup(s => s.JoinGameAsync(It.IsAny<JoinGamePacket>(), It.IsAny<string>())).ReturnsAsync(result);
        return (new JoinPortal(sessions.Object, router.Object), sender.Sent);
    }

    private sealed class JoinPortal : AltruistGameSessionPortal
    {
        public JoinPortal(IGameSessionService sessions, IAltruistRouter router)
            : base(sessions, router, Enumerable.Empty<IClientSessionCleanup>(), NullLoggerFactory.Instance)
        {
        }
    }

    private sealed class RecordingClientSender : ClientSender
    {
        public List<IPacketBase> Sent { get; } = new();

        public RecordingClientSender() : base(Mock.Of<IConnectionStore>(), Mock.Of<ICodec>())
        {
        }

        public override Task SendAsync<TPacketBase>(string clientId, TPacketBase message)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }
    }
}
