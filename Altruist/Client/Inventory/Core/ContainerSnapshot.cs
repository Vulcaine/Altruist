namespace Altruist.Client.Inventory;

/// <summary>
/// Per-container in-memory mirror of slot state. Wraps a layout (geometry)
/// with the anchor table and the multi-cell coverage map, so queries like
/// "what item covers cell N?" are O(1) instead of an O(items) scan.
///
/// <para>The wire only carries anchor cells; the mirror reconstructs the
/// W×H footprint locally on every <see cref="ApplySnapshot"/> call. This
/// keeps the wire compact AND keeps coverage internally consistent — there
/// is no way to receive an item without its coverage being recomputed.</para>
///
/// <para>Thread-affinity: not thread-safe. Consumers (e.g. Unity's main
/// thread) drain inbound packets serially and only touch this from a single
/// thread, so locking would only add cost.</para>
/// </summary>
internal sealed class ContainerSnapshot
{
    /// <summary>Geometry this snapshot validates against.</summary>
    public ContainerLayout Layout { get; }

    // anchor cell → snapshot. Linked cells are NOT keys here.
    private readonly Dictionary<ushort, ItemSnapshot> _anchors = new();

    // covered cell (anchor or any linked cell) → anchor cell.
    // Lookup direction: "what's covering cell N?" → anchor → snapshot.
    private readonly Dictionary<ushort, ushort> _coverage = new();

    /// <summary>Creates an empty mirror for <paramref name="layout"/>.</summary>
    /// <param name="layout">Container geometry.</param>
    public ContainerSnapshot(ContainerLayout layout)
    {
        Layout = layout ?? throw new ArgumentNullException(nameof(layout));
    }

    /// <summary>Live view of all anchored items.</summary>
    public IReadOnlyCollection<ItemSnapshot> Anchors => _anchors.Values;

    /// <summary>Item anchored exactly at <paramref name="cell"/>, or <c>null</c>.</summary>
    /// <param name="cell">Cell index.</param>
    public ItemSnapshot? GetAnchor(ushort cell)
        => _anchors.TryGetValue(cell, out var snap) ? snap : null;

    /// <summary>Item whose footprint covers <paramref name="cell"/>, or <c>null</c>.</summary>
    /// <param name="cell">Cell index.</param>
    public ItemSnapshot? GetItemCoveringCell(ushort cell)
    {
        if (_coverage.TryGetValue(cell, out var anchor) && _anchors.TryGetValue(anchor, out var snap))
            return snap;
        return null;
    }

    /// <summary>
    /// Apply a non-empty snapshot at its anchor. Recomputes the coverage map
    /// for the new footprint after first clearing any prior entry at the same
    /// anchor (size may have changed). Returns true if the apply succeeded;
    /// false means the anchor or footprint was rejected (out of bounds for
    /// the layout, or anchor index invalid).
    /// </summary>
    public bool ApplySnapshot(ItemSnapshot snap)
    {
        if (snap.IsEmpty) return ClearAnchor(snap.Cell);

        if (!IsAnchorValid(snap.Cell, snap.Width, snap.Height))
            return false;

        // If something was at this anchor before, drop its coverage entries
        // before laying the new footprint down.
        ClearCoverageFor(snap.Cell);

        _anchors[snap.Cell] = snap;
        WriteCoverage(snap);
        return true;
    }

    /// <summary>
    /// Clear the slot at the given anchor cell. If <paramref name="cell"/>
    /// is a linked cell rather than an anchor, the call is a no-op (server
    /// authoritative model: linked cells never get standalone clears). Returns
    /// true if an anchor was actually removed.
    /// </summary>
    public bool ClearAnchor(ushort cell)
    {
        if (!_anchors.ContainsKey(cell)) return false;
        ClearCoverageFor(cell);
        _anchors.Remove(cell);
        return true;
    }

    /// <summary>
    /// Read-only mirror of <c>GridStorage.CanFit</c>: checks every cell in
    /// the W×H footprint at <paramref name="anchorCell"/> for an existing
    /// occupant. Cells whose anchor matches one of <paramref name="ignoreAnchors"/>
    /// are treated as free — needed for swap/move pre-validation where we
    /// want to know "can this fit if I first remove these other anchors?".
    /// </summary>
    public bool CanAnchorItemAt(ushort anchorCell, int width, int height, ReadOnlySpan<ushort> ignoreAnchors)
    {
        if (width <= 0 || height <= 0) return false;
        if (!IsAnchorValid(anchorCell, width, height)) return false;

        foreach (var (cell, _) in EnumerateFootprint(anchorCell, width, height))
        {
            if (!_coverage.TryGetValue(cell, out var occupantAnchor)) continue;
            if (ContainsAnchor(ignoreAnchors, occupantAnchor)) continue;
            return false;
        }
        return true;
    }

    /// <summary>Lowest uncovered cell index, or -1 when full.</summary>
    public int FindFirstFreeCell()
    {
        int capacity = Layout switch
        {
            GridLayout g => g.Capacity,
            EquipmentLayout e => e.Capacity,
            _ => 0,
        };
        for (int cell = 0; cell < capacity; cell++)
        {
            if (!_coverage.ContainsKey((ushort)cell)) return cell;
        }
        return -1;
    }

    private void WriteCoverage(ItemSnapshot snap)
    {
        foreach (var (cell, _) in EnumerateFootprint(snap.Cell, snap.Width, snap.Height))
            _coverage[cell] = snap.Cell;
    }

    private void ClearCoverageFor(ushort anchor)
    {
        // Remove every coverage entry pointing back to this anchor. Cheap
        // because we only ever store entries this small dict knows about,
        // and we never see more than ~200 cells in practice.
        var toRemove = new List<ushort>();
        foreach (var kv in _coverage)
        {
            if (kv.Value == anchor) toRemove.Add(kv.Key);
        }
        foreach (var k in toRemove) _coverage.Remove(k);
    }

    /// <summary>
    /// Walk every cell in the W×H footprint anchored at <paramref name="anchorCell"/>.
    /// For grids, footprint extends right + down; equipment containers always
    /// produce just the single anchor cell.
    /// </summary>
    private IEnumerable<(ushort cell, int dx)> EnumerateFootprint(ushort anchorCell, int w, int h)
    {
        switch (Layout)
        {
            case GridLayout grid:
                if (!grid.TryToCoords(anchorCell, out short ax, out short ay)) yield break;
                for (int dy = 0; dy < h; dy++)
                {
                    for (int dx = 0; dx < w; dx++)
                    {
                        int cx = ax + dx;
                        int cy = ay + dy;
                        if (cx >= grid.Columns || cy >= grid.Rows) continue;
                        yield return (grid.ToCell((short)cx, (short)cy), dx);
                    }
                }
                break;

            case EquipmentLayout equip:
                if (anchorCell < equip.Capacity)
                    yield return (anchorCell, 0);
                break;
        }
    }

    private bool IsAnchorValid(ushort anchorCell, int width, int height)
    {
        switch (Layout)
        {
            case GridLayout grid:
                if (!grid.TryToCoords(anchorCell, out short ax, out short ay)) return false;
                if (ax + width > grid.Columns) return false;
                if (ay + height > grid.Rows) return false;
                return true;

            case EquipmentLayout equip:
                // Equipment slots are by-name, not by-grid: the slot itself is
                // intrinsic 1×1, but the item's natural Width/Height (e.g. a
                // sword's 1×2 inventory footprint) rides along on the wire as
                // metadata. Coverage stays a single cell — see EnumerateFootprint.
                return anchorCell < equip.Capacity;

            default:
                return false;
        }
    }

    private static bool ContainsAnchor(ReadOnlySpan<ushort> anchors, ushort target)
    {
        for (int i = 0; i < anchors.Length; i++)
        {
            if (anchors[i] == target) return true;
        }
        return false;
    }
}
