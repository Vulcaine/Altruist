using Altruist;
using Altruist.Client;
using Altruist.Client.Inventory;
using Altruist.Client.Inventory.Packets;
using MessagePack;
using MessagePack.Resolvers;

namespace Tests.Altruist.Client.Inventory;

/// <summary>
/// Pins the bug class that caused most of this session's pain: items at row
/// 1+ collapsing onto row 0 because the wire cell index was being computed
/// as <c>X</c> instead of <c>Y*Cols+X</c>. The mirror's coverage map is the
/// last line of defense — a wrong-cell snapshot must NOT silently overwrite
/// a different row's item.
///
/// <para>These tests reproduce the exact scenario through the dispatcher:
/// multiple items at distinct grid positions, all delivered correctly,
/// with non-overlapping coverage afterwards.</para>
/// </summary>
public sealed class MultiCellRegressionTests
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
    public void ItemsAtDifferentGridRows_EndUpAtDistinctMirrorCells()
    {
        var dispatcher = new ClientPacketDispatcher();
        var service = new ClientInventoryService();
        service.RegisterContainer(new GridLayout(Window, 5, 9));
        dispatcher.Register(service);
        var codec = new MessagePackClientCodec();

        // The exact placements that triggered the row-collapse bug:
        //   (col=0, row=0) → cell 0
        //   (col=2, row=1) → cell 7
        //   (col=4, row=4) → cell 24
        //   (col=0, row=8) → cell 40
        var placements = new (ushort Cell, byte W, byte H, string Key)[]
        {
            (0,  1, 3, "sword"),
            (7,  1, 2, "armor"),
            (24, 1, 1, "trinket"),
            (40, 1, 1, "scroll"),
        };

        foreach (var (cell, w, h, key) in placements)
        {
            dispatcher.Dispatch(EncodeEnvelope(new ItemSnapshotPacket
            {
                Window = Window,
                Cell = cell,
                ItemKey = key,
                Count = 1,
                Width = w,
                Height = h,
            }), codec);
        }

        // Every placed key must be distinct in the mirror — no overwriting.
        var anchors = service.GetAllItems(Window);
        Assert.Equal(placements.Length, anchors.Count);
        foreach (var p in placements)
            Assert.Contains(anchors, s => s.ItemKey == p.Key && s.Cell == p.Cell);
    }

    [Fact]
    public void Sword1x3AtCell0_DoesNotCoverCellsAtRow1Column2()
    {
        // Specifically: the sword at (col=0, row=0) covers {0, 5, 10}.
        // The pre-fix bug delivered ALL items as cell index < 5, so a sword
        // at column 0 would have collided with anything in column 2 of the
        // same row. Pin that the mirror does NOT report cell 7 (col=2,
        // row=1) as covered by the sword.
        var svc = new ClientInventoryService();
        svc.RegisterContainer(new GridLayout(Window, 5, 9));

        svc.OnItemSnapshot(new ItemSnapshotPacket
        {
            Window = Window, Cell = 0, ItemKey = "sword", Count = 1, Width = 1, Height = 3,
        });

        Assert.NotNull(svc.GetItemCoveringCell(Window, 0));
        Assert.NotNull(svc.GetItemCoveringCell(Window, 5));
        Assert.NotNull(svc.GetItemCoveringCell(Window, 10));

        // The pre-fix collapse would have shoved a row-1 item into row 0
        // at col 2 (cell 2 instead of cell 7). Cells 7 and 12 (real row 1
        // and row 2 col 2) must remain free in the post-fix world.
        Assert.Null(svc.GetItemCoveringCell(Window, 2));
        Assert.Null(svc.GetItemCoveringCell(Window, 7));
        Assert.Null(svc.GetItemCoveringCell(Window, 12));
    }

    [Fact]
    public void ItemAtBottomRow_RoundTrips_WithoutCoordinateLoss()
    {
        // The pre-fix wire would have shipped (col=0, row=8) as cell 0.
        // The mirror must place it at cell 40 instead. End-to-end through
        // the dispatcher to make sure the wire encoding is preserved.
        var dispatcher = new ClientPacketDispatcher();
        var service = new ClientInventoryService();
        service.RegisterContainer(new GridLayout(Window, 5, 9));
        dispatcher.Register(service);

        dispatcher.Dispatch(EncodeEnvelope(new ItemSnapshotPacket
        {
            Window = Window,
            Cell = 40,
            ItemKey = "scroll",
            Count = 1,
        }), new MessagePackClientCodec());

        Assert.Equal("scroll", service.GetSnapshot(Window, 40)!.ItemKey);
        // Cell 0 must remain free — the bug would have aliased cell 40 onto cell 0.
        Assert.Null(service.GetSnapshot(Window, 0));
    }
}
