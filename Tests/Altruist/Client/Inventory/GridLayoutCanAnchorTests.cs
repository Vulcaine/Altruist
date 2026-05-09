using Altruist.Client.Inventory;
using Altruist.Client.Inventory.Packets;

namespace Tests.Altruist.Client.Inventory;

/// <summary>
/// <see cref="ClientInventoryService.CanAnchorItemAt"/> is the read-only
/// half of <c>GridStorage.CanFit</c>. Drag/drop validation calls it; getting
/// it wrong means either silent illegal-drops or refusing legal swaps.
/// </summary>
public sealed class GridLayoutCanAnchorTests
{
    const byte Window = 1;

    private static ClientInventoryService NewBag(short cols = 5, short rows = 9)
    {
        var svc = new ClientInventoryService();
        svc.RegisterContainer(new GridLayout(Window, cols, rows));
        return svc;
    }

    private static void Place(ClientInventoryService svc, ushort anchor, byte w, byte h)
        => svc.OnItemSnapshot(new ItemSnapshotPacket
        {
            Window = Window,
            Cell = anchor,
            ItemKey = "occupant",
            Count = 1,
            Width = w,
            Height = h,
        });

    [Fact]
    public void EmptyGrid_AnyInBoundsAnchor_FitsWithRoomToSpare()
    {
        var svc = NewBag();
        for (ushort cell = 0; cell < 45; cell++)
            Assert.True(svc.CanAnchorItemAt(Window, cell, 1, 1),
                $"1×1 at cell {cell} should fit in an empty 5×9 grid");
    }

    [Fact]
    public void OneByThree_FitsAtRows0Through6_AndIsRejectedAtRows7And8()
    {
        var svc = NewBag();
        // Anchored at (col, row=0..6) → footprint extends to row=2..8 → fits.
        for (short row = 0; row <= 6; row++)
        for (short col = 0; col < 5; col++)
            Assert.True(svc.CanAnchorItemAt(Window, (ushort)(row * 5 + col), 1, 3),
                $"1×3 should fit at (col={col}, row={row})");

        // row=7 → extends to row 9 (out). row=8 → extends to row 10 (out).
        for (short row = 7; row <= 8; row++)
        for (short col = 0; col < 5; col++)
            Assert.False(svc.CanAnchorItemAt(Window, (ushort)(row * 5 + col), 1, 3),
                $"1×3 should be rejected at (col={col}, row={row}) — extends past grid");
    }

    [Fact]
    public void HorizontalOverflow_IsRejected()
    {
        var svc = NewBag();
        // 2×1 at col=4 would extend into col=5, which doesn't exist.
        Assert.False(svc.CanAnchorItemAt(Window, anchorCell: 4, width: 2, height: 1));
        Assert.True(svc.CanAnchorItemAt(Window, anchorCell: 3, width: 2, height: 1));
    }

    [Fact]
    public void OccupiedCell_BlocksAnchor()
    {
        var svc = NewBag();
        Place(svc, anchor: 12, w: 1, h: 1);

        Assert.False(svc.CanAnchorItemAt(Window, 12, 1, 1));
        // 1×3 anchored at 2 would cover {2, 7, 12} — blocked by occupant at 12.
        Assert.False(svc.CanAnchorItemAt(Window, 2, 1, 3));
    }

    [Fact]
    public void IgnoreAnchors_ExcludesSourcesOwnFootprint_FromCollisionCheck()
    {
        var svc = NewBag();
        // 1×3 sword anchored at cell 0 covers {0, 5, 10}.
        Place(svc, anchor: 0, w: 1, h: 3);

        // Without ignoreAnchors, dropping the same item at cell 0 would
        // collide with itself.
        Assert.False(svc.CanAnchorItemAt(Window, 0, 1, 3));

        // With ignoreAnchors=[0], the source's own footprint is skipped, so
        // the move-to-self check returns true (drag-validation needs this).
        Assert.True(svc.CanAnchorItemAt(Window, 0, 1, 3, ignoreAnchors: 0));

        // Anchor cell 5 would cover {5, 10, 15}. With ignoreAnchors=[0] the
        // occupant at 0/5/10 is treated as free, so the new anchor fits.
        Assert.True(svc.CanAnchorItemAt(Window, 5, 1, 3, ignoreAnchors: 0));
    }

    [Fact]
    public void IgnoreAnchors_OnlyExcludesNamedAnchors_NotOthers()
    {
        var svc = NewBag();
        Place(svc, anchor: 0, w: 1, h: 3); // covers {0, 5, 10}
        Place(svc, anchor: 1, w: 1, h: 1); // covers {1}

        // Trying to land 2×1 at cell 0 — covers {0, 1}.
        // Both occupants block. Ignoring just anchor 0 isn't enough; we'd
        // also need to ignore anchor 1 to make this fit.
        Assert.False(svc.CanAnchorItemAt(Window, 0, 2, 1, ignoreAnchors: 0));
        Assert.True(svc.CanAnchorItemAt(Window, 0, 2, 1, ignoreAnchors: new ushort[] { 0, 1 }));
    }

    [Fact]
    public void UnknownWindow_ReturnsFalse()
    {
        var svc = NewBag();
        // Window 99 was never registered — must not blow up, must say "no".
        Assert.False(svc.CanAnchorItemAt(window: 99, anchorCell: 0, 1, 1));
    }

    [Fact]
    public void ZeroOrNegativeSize_IsRejected()
    {
        var svc = NewBag();
        Assert.False(svc.CanAnchorItemAt(Window, 0, 0, 1));
        Assert.False(svc.CanAnchorItemAt(Window, 0, 1, 0));
        Assert.False(svc.CanAnchorItemAt(Window, 0, -1, 1));
    }
}
