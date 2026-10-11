namespace Altruist.Client.Inventory;

/// <summary>
/// Describes the geometry of a single inventory window. Registered by the
/// consumer at boot via <see cref="IClientInventoryService.RegisterContainer"/>;
/// the mirror is dimension-less until it learns the layout.
///
/// <para>Two shapes are supported, mirroring the server-side primitives:
/// <see cref="GridLayout"/> (Diablo-style multi-cell grid) and
/// <see cref="EquipmentLayout"/> (named-slot, always 1×1).</para>
/// </summary>
/// <param name="Window">Consumer-defined container id; matches <c>Window</c> on the inventory packets.</param>
public abstract record ContainerLayout(byte Window);

/// <summary>
/// Multi-cell grid container. Wire cell index is <c>Y * Columns + X</c>; the
/// mirror uses this to translate between the flat wire cell and the (X, Y)
/// grid coordinates needed for multi-cell coverage tracking.
/// </summary>
/// <remarks>Use for bags / stashes where items occupy W×H cells; items extend right (+X)
/// and down (+Y) from their anchor. For fixed named slots use <see cref="EquipmentLayout"/>.</remarks>
/// <param name="Window">Container id.</param>
/// <param name="Columns">Grid width in cells.</param>
/// <param name="Rows">Grid height in cells.</param>
public sealed record GridLayout(byte Window, short Columns, short Rows) : ContainerLayout(Window)
{
    /// <summary>Total cell count (<c>Columns * Rows</c>).</summary>
    public int Capacity => Columns * Rows;

    /// <summary>Converts a flat cell index to grid coordinates.</summary>
    /// <param name="cell">Flat cell index.</param>
    /// <param name="x">Column (0-based), or 0 on failure.</param>
    /// <param name="y">Row (0-based), or 0 on failure.</param>
    /// <returns><c>false</c> when the cell is outside the grid or the grid has no columns.</returns>
    public bool TryToCoords(ushort cell, out short x, out short y)
    {
        if (Columns <= 0 || cell >= Capacity)
        {
            x = 0; y = 0;
            return false;
        }
        x = (short)(cell % Columns);
        y = (short)(cell / Columns);
        return true;
    }

    /// <summary>Converts grid coordinates to a flat cell index (<c>y * Columns + x</c>); no bounds check.</summary>
    /// <param name="x">Column (0-based).</param>
    /// <param name="y">Row (0-based).</param>
    /// <returns>The flat cell index.</returns>
    public ushort ToCell(short x, short y) => (ushort)(y * Columns + x);
}

/// <summary>
/// Named-slot equipment container. Each slot is 1×1 and identified by a name
/// ("weapon", "head", etc.); <c>cell</c> is the index into
/// <see cref="SlotNames"/>. An item's Width/Height are kept as metadata but it
/// always covers just its single slot; <see cref="IClientInventoryService.CanAnchorItemAt"/>
/// only checks that the slot index is in range and free.
/// </summary>
/// <param name="Window">Container id.</param>
/// <param name="SlotNames">Slot names; the list index is the cell index.</param>
public sealed record EquipmentLayout(byte Window, IReadOnlyList<string> SlotNames) : ContainerLayout(Window)
{
    /// <summary>Number of slots (<c>SlotNames.Count</c>).</summary>
    public int Capacity => SlotNames.Count;
}
