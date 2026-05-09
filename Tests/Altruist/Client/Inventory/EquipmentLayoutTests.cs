using Altruist.Client.Inventory;
using Altruist.Client.Inventory.Packets;

namespace Tests.Altruist.Client.Inventory;

/// <summary>
/// Equipment containers are flat name-indexed (1×1 only). Multi-cell items
/// must be rejected, and named-slot lookup must round-trip via the cell
/// index assigned by the consumer.
/// </summary>
public sealed class EquipmentLayoutTests
{
    const byte Window = 2;

    private static (ClientInventoryService svc, EquipmentLayout layout) NewEquipment()
    {
        var svc = new ClientInventoryService();
        var layout = new EquipmentLayout(Window, new[]
        {
            "weapon", "body", "head", "feet", "wrist", "neck", "ear", "shield",
        });
        svc.RegisterContainer(layout);
        return (svc, layout);
    }

    [Fact]
    public void EquipmentSlots_AcceptOneByOneItems()
    {
        var (svc, _) = NewEquipment();
        svc.OnItemSnapshot(new ItemSnapshotPacket
        {
            Window = Window,
            Cell = 0, // weapon
            ItemKey = "sword",
            Count = 1,
            Width = 1,
            Height = 1,
        });

        Assert.NotNull(svc.GetSnapshot(Window, 0));
        Assert.Equal("sword", svc.GetSnapshot(Window, 0)!.ItemKey);
    }

    [Fact]
    public void EquipmentSlots_RejectMultiCellItems()
    {
        var (svc, _) = NewEquipment();

        // The mirror's CanAnchorItemAt must say "no" for any W>1 or H>1
        // against an equipment layout — the wire would never deliver a
        // multi-cell equipment slot, and a misrouted packet shouldn't
        // corrupt state.
        Assert.False(svc.CanAnchorItemAt(Window, 0, 1, 2));
        Assert.False(svc.CanAnchorItemAt(Window, 0, 2, 1));

        // ApplySnapshot itself must reject the multi-cell shape — sending
        // it should NOT populate the cell.
        svc.OnItemSnapshot(new ItemSnapshotPacket
        {
            Window = Window,
            Cell = 0,
            ItemKey = "broken_multicell_weapon",
            Count = 1,
            Width = 1,
            Height = 3,
        });

        Assert.Null(svc.GetSnapshot(Window, 0));
    }

    [Fact]
    public void OutOfBoundsEquipmentCell_IsRejected()
    {
        var (svc, _) = NewEquipment();
        // Layout has 8 slots (indices 0..7). Cell 8 is past the end.
        svc.OnItemSnapshot(new ItemSnapshotPacket
        {
            Window = Window,
            Cell = 8,
            ItemKey = "hat",
            Count = 1,
        });

        Assert.Null(svc.GetSnapshot(Window, 8));
        Assert.False(svc.CanAnchorItemAt(Window, 8, 1, 1));
    }

    [Fact]
    public void NamedSlots_AreIndependent()
    {
        var (svc, _) = NewEquipment();
        svc.OnItemSnapshot(new ItemSnapshotPacket { Window = Window, Cell = 0, ItemKey = "sword", Count = 1 });
        svc.OnItemSnapshot(new ItemSnapshotPacket { Window = Window, Cell = 7, ItemKey = "shield", Count = 1 });

        Assert.Equal("sword", svc.GetSnapshot(Window, 0)!.ItemKey);
        Assert.Equal("shield", svc.GetSnapshot(Window, 7)!.ItemKey);

        // Clearing one must not disturb the other.
        svc.OnItemSnapshot(new ItemSnapshotPacket { Window = Window, Cell = 0, ItemKey = "", Count = 0 });
        Assert.Null(svc.GetSnapshot(Window, 0));
        Assert.Equal("shield", svc.GetSnapshot(Window, 7)!.ItemKey);
    }

    [Fact]
    public void FindFirstFreeCell_SkipsOccupiedSlots()
    {
        var (svc, _) = NewEquipment();
        svc.OnItemSnapshot(new ItemSnapshotPacket { Window = Window, Cell = 0, ItemKey = "sword", Count = 1 });
        svc.OnItemSnapshot(new ItemSnapshotPacket { Window = Window, Cell = 1, ItemKey = "armor", Count = 1 });

        Assert.Equal(2, svc.FindFirstFreeCell(Window));
    }
}
