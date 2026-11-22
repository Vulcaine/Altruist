using Altruist.Client.Inventory;
using Altruist.Client.Inventory.Packets;

namespace Tests.Altruist.Client.Inventory;

/// <summary>
/// Multi-cell coverage invariants for grid containers. The wire only carries
/// anchor cells; the mirror reconstructs which cells are linked from the
/// W×H footprint. These tests pin that reconstruction so a refactor doesn't
/// drift the linked-cell math without us noticing.
/// </summary>
public sealed class GridLayoutCoverageTests
{
    const byte Window = 1;
    const short Cols = 5;
    const short Rows = 9;

    private static (ClientInventoryService svc, GridLayout grid) NewBag()
    {
        var svc = new ClientInventoryService();
        var grid = new GridLayout(Window, Cols, Rows);
        svc.RegisterContainer(grid);
        return (svc, grid);
    }

    private static void Place(ClientInventoryService svc, ushort anchor, byte w, byte h, string key = "item")
        => svc.OnItemSnapshot(new ItemSnapshotPacket
        {
            Window = Window,
            Cell = anchor,
            ItemKey = key,
            Count = 1,
            Width = w,
            Height = h,
        });

    [Fact]
    public void SingleCellItem_OccupiesExactlyTheAnchorCell()
    {
        var (svc, _) = NewBag();
        Place(svc, anchor: 7, w: 1, h: 1);

        Assert.NotNull(svc.GetSnapshot(Window, 7));
        Assert.NotNull(svc.GetItemCoveringCell(Window, 7));
        Assert.Null(svc.GetItemCoveringCell(Window, 6));
        Assert.Null(svc.GetItemCoveringCell(Window, 8));
        Assert.Null(svc.GetItemCoveringCell(Window, 12)); // cell directly below
    }

    [Fact]
    public void OneByThree_LightsUpAllThreeCellsBelowTheAnchor()
    {
        var (svc, _) = NewBag();
        // 1×3 sword anchored at (col=0, row=0). Footprint = cells 0, 5, 10.
        Place(svc, anchor: 0, w: 1, h: 3, key: "sword");

        var anchor = svc.GetSnapshot(Window, 0);
        Assert.NotNull(anchor);
        Assert.Equal("sword", anchor!.ItemKey);

        // Linked cells return the SAME snapshot (anchor lookup), not nulls.
        Assert.Same(anchor, svc.GetItemCoveringCell(Window, 0));
        Assert.Same(anchor, svc.GetItemCoveringCell(Window, 5));
        Assert.Same(anchor, svc.GetItemCoveringCell(Window, 10));

        // Surrounding cells stay free.
        Assert.Null(svc.GetItemCoveringCell(Window, 1));
        Assert.Null(svc.GetItemCoveringCell(Window, 6));
        Assert.Null(svc.GetItemCoveringCell(Window, 15));

        // GetSnapshot only finds anchors, not linked cells.
        Assert.Null(svc.GetSnapshot(Window, 5));
        Assert.Null(svc.GetSnapshot(Window, 10));
    }

    [Fact]
    public void TwoByTwo_CoversTheFullSquare()
    {
        var (svc, _) = NewBag();
        // 2×2 at (col=2, row=3). Footprint = cells 17, 18, 22, 23.
        Place(svc, anchor: 17, w: 2, h: 2, key: "armor");

        Assert.NotNull(svc.GetItemCoveringCell(Window, 17));
        Assert.NotNull(svc.GetItemCoveringCell(Window, 18));
        Assert.NotNull(svc.GetItemCoveringCell(Window, 22));
        Assert.NotNull(svc.GetItemCoveringCell(Window, 23));

        // Edges of the footprint
        Assert.Null(svc.GetItemCoveringCell(Window, 19));
        Assert.Null(svc.GetItemCoveringCell(Window, 16));
        Assert.Null(svc.GetItemCoveringCell(Window, 12));
    }

    [Fact]
    public void Clearing_AnAnchor_ReleasesAllLinkedCells()
    {
        var (svc, _) = NewBag();
        Place(svc, anchor: 0, w: 1, h: 3, key: "sword");

        // Clear via empty snapshot (the wire-equivalent of slot clear).
        svc.OnItemSnapshot(new ItemSnapshotPacket { Window = Window, Cell = 0, ItemKey = "", Count = 0 });

        Assert.Null(svc.GetSnapshot(Window, 0));
        Assert.Null(svc.GetItemCoveringCell(Window, 0));
        Assert.Null(svc.GetItemCoveringCell(Window, 5));
        Assert.Null(svc.GetItemCoveringCell(Window, 10));
    }

    [Fact]
    public void Clearing_AnchorA_DoesNotDisturb_AnchorBLinkedCells()
    {
        var (svc, _) = NewBag();
        // A: 1×3 at cell 0 covers {0, 5, 10}.
        // B: 1×3 at cell 1 covers {1, 6, 11}.
        Place(svc, anchor: 0, w: 1, h: 3, key: "swordA");
        Place(svc, anchor: 1, w: 1, h: 3, key: "swordB");

        // Clear A.
        svc.OnItemSnapshot(new ItemSnapshotPacket { Window = Window, Cell = 0, ItemKey = "", Count = 0 });

        // B's coverage must be untouched.
        Assert.NotNull(svc.GetItemCoveringCell(Window, 1));
        Assert.NotNull(svc.GetItemCoveringCell(Window, 6));
        Assert.NotNull(svc.GetItemCoveringCell(Window, 11));
        Assert.Equal("swordB", svc.GetItemCoveringCell(Window, 6)!.ItemKey);
    }

    [Fact]
    public void ResizingAtTheSameAnchor_DropsTheOldFootprint()
    {
        var (svc, _) = NewBag();
        // First: 1×3 at cell 0 covers {0, 5, 10}.
        Place(svc, anchor: 0, w: 1, h: 3, key: "sword");
        Assert.NotNull(svc.GetItemCoveringCell(Window, 10));

        // Replace with 1×1 at the same anchor. The old cells 5 and 10 must
        // release; only cell 0 stays covered.
        Place(svc, anchor: 0, w: 1, h: 1, key: "dagger");

        Assert.NotNull(svc.GetItemCoveringCell(Window, 0));
        Assert.Null(svc.GetItemCoveringCell(Window, 5));
        Assert.Null(svc.GetItemCoveringCell(Window, 10));
    }

    [Fact]
    public void OutOfBoundsAnchor_IsRejected_AndLeavesStateUntouched()
    {
        var (svc, _) = NewBag();
        // 1×3 anchored at row 7 (cell 35) would extend past the 9-row grid.
        Place(svc, anchor: 35, w: 1, h: 3, key: "tooTall");

        Assert.Null(svc.GetSnapshot(Window, 35));
        Assert.Null(svc.GetItemCoveringCell(Window, 35));
        Assert.Null(svc.GetItemCoveringCell(Window, 40));
    }

    [Fact]
    public void GetAllItems_ReturnsAnchorsOnly_NotLinkedCells()
    {
        var (svc, _) = NewBag();
        Place(svc, anchor: 0, w: 1, h: 3, key: "sword");
        Place(svc, anchor: 7, w: 1, h: 1, key: "potion");

        var all = svc.GetAllItems(Window);
        Assert.Equal(2, all.Count);
        Assert.Contains(all, s => s.ItemKey == "sword");
        Assert.Contains(all, s => s.ItemKey == "potion");
    }
}
