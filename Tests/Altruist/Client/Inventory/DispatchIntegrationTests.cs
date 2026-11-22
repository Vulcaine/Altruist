using Altruist;
using Altruist.Client;
using Altruist.Client.Inventory;
using Altruist.Client.Inventory.Events;
using Altruist.Client.Inventory.Packets;
using MessagePack;
using MessagePack.Resolvers;

namespace Tests.Altruist.Client.Inventory;

/// <summary>
/// End-to-end: a wire-encoded envelope flowing through the real
/// <see cref="ClientPacketDispatcher"/> reaches the
/// <see cref="ClientInventoryService"/> handler, populates the cache, and
/// fires the outbound event. Catches regressions where the [Packet]
/// auto-discovery, the codec, or the event wiring drifts apart.
/// </summary>
public sealed class DispatchIntegrationTests
{
    const byte Window = 1;

    private static readonly MessagePackSerializerOptions ServerOptions =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                StandardResolverAllowPrivate.Instance,
                TypelessContractlessStandardResolver.Instance));

    private static byte[] EncodeEnvelope<T>(T pkt) where T : IPacketBase =>
        MessagePackSerializer.Serialize(new MessageEnvelope(pkt, "rcv"), ServerOptions);

    [Fact]
    public void DispatchedSnapshot_PopulatesMirror_AndFiresChangedEvent()
    {
        var dispatcher = new ClientPacketDispatcher();
        var service = new ClientInventoryService();
        service.RegisterContainer(new GridLayout(Window, 5, 9));
        dispatcher.Register(service);

        SlotChangedEvent? observed = null;
        service.SlotChanged += e => observed = e;

        var pkt = new ItemSnapshotPacket
        {
            Window = Window,
            Cell = 7,
            ItemKey = "potion",
            Count = 5,
            Width = 1,
            Height = 1,
            Stackable = true,
        };
        dispatcher.Dispatch(EncodeEnvelope(pkt), new MessagePackClientCodec());

        Assert.NotNull(observed);
        Assert.Equal("potion", observed!.Snapshot.ItemKey);
        Assert.Equal(5, observed.Snapshot.Count);

        var cached = service.GetSnapshot(Window, 7);
        Assert.NotNull(cached);
        Assert.Equal("potion", cached!.ItemKey);
        Assert.Equal(5, cached.Count);
    }

    [Fact]
    public void DispatchedClear_RemovesFromMirror_AndFiresClearedEvent()
    {
        var dispatcher = new ClientPacketDispatcher();
        var service = new ClientInventoryService();
        service.RegisterContainer(new GridLayout(Window, 5, 9));
        dispatcher.Register(service);

        // Place first.
        dispatcher.Dispatch(EncodeEnvelope(new ItemSnapshotPacket
        {
            Window = Window,
            Cell = 3,
            ItemKey = "potion",
            Count = 1,
        }), new MessagePackClientCodec());
        Assert.NotNull(service.GetSnapshot(Window, 3));

        // Then clear via the dedicated cleared packet.
        SlotClearedEvent? cleared = null;
        service.SlotCleared += e => cleared = e;

        dispatcher.Dispatch(EncodeEnvelope(new ItemSlotClearedPacket
        {
            Window = Window,
            Cell = 3,
        }), new MessagePackClientCodec());

        Assert.NotNull(cleared);
        Assert.Equal(Window, cleared!.Window);
        Assert.Equal(3, cleared.Cell);
        Assert.Null(service.GetSnapshot(Window, 3));
    }

    [Fact]
    public void DispatchedSnapshot_WithEmptyKey_IsTreatedAsClear()
    {
        var dispatcher = new ClientPacketDispatcher();
        var service = new ClientInventoryService();
        service.RegisterContainer(new GridLayout(Window, 5, 9));
        dispatcher.Register(service);

        // Place.
        dispatcher.Dispatch(EncodeEnvelope(new ItemSnapshotPacket
        {
            Window = Window, Cell = 2, ItemKey = "x", Count = 1,
        }), new MessagePackClientCodec());

        SlotClearedEvent? cleared = null;
        service.SlotCleared += e => cleared = e;

        // Empty-key snapshot — the legacy "send SItemSet with Item=''" shape.
        // The mirror must accept this as a clear, not stash an empty item.
        dispatcher.Dispatch(EncodeEnvelope(new ItemSnapshotPacket
        {
            Window = Window, Cell = 2, ItemKey = "", Count = 0,
        }), new MessagePackClientCodec());

        Assert.NotNull(cleared);
        Assert.Equal(2, cleared!.Cell);
        Assert.Null(service.GetSnapshot(Window, 2));
    }

    [Fact]
    public void OpaquePayload_SurvivesDispatch_Untouched()
    {
        var dispatcher = new ClientPacketDispatcher();
        var service = new ClientInventoryService();
        service.RegisterContainer(new GridLayout(Window, 5, 9));
        dispatcher.Register(service);

        ItemSnapshot? observed = null;
        service.SlotChanged += e => observed = e.Snapshot;

        var payload = new byte[] { 0x01, 0x02, 0x03, 0xAA, 0xBB, 0xCC };
        dispatcher.Dispatch(EncodeEnvelope(new ItemSnapshotPacket
        {
            Window = Window,
            Cell = 11,
            ItemKey = "sword+5",
            Count = 1,
            Width = 1,
            Height = 3,
            OpaquePayload = payload,
        }), new MessagePackClientCodec());

        Assert.NotNull(observed);
        Assert.Equal(payload, observed!.OpaquePayload);
    }

    [Fact]
    public void Snapshot_ForUnregisteredWindow_IsDropped_NoEvents()
    {
        var dispatcher = new ClientPacketDispatcher();
        var service = new ClientInventoryService();
        // No RegisterContainer call — window 1 is unknown.
        dispatcher.Register(service);

        bool fired = false;
        service.SlotChanged += _ => fired = true;
        service.SlotCleared += _ => fired = true;

        dispatcher.Dispatch(EncodeEnvelope(new ItemSnapshotPacket
        {
            Window = 1, Cell = 0, ItemKey = "x", Count = 1,
        }), new MessagePackClientCodec());

        Assert.False(fired, "snapshots for unregistered windows must be silently dropped");
    }
}
