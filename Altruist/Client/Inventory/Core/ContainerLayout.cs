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
public abstract record ContainerLayout(byte Window);

/// <summary>
/// Multi-cell grid container. Wire cell index is <c>Y * Columns + X</c>; the
/// mirror uses this to translate between the flat wire cell and the (X, Y)
/// grid coordinates needed for multi-cell coverage tracking.
/// </summary>
public sealed record GridLayout(byte Window, short Columns, short Rows) : ContainerLayout(Window)
{
    public int Capacity => Columns * Rows;

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

    public ushort ToCell(short x, short y) => (ushort)(y * Columns + x);
}

/// <summary>
/// Named-slot equipment container. Each slot is 1×1 and identified by a name
/// ("weapon", "head", etc.); <c>cell</c> is the index into
/// <see cref="SlotNames"/>. Multi-cell items addressed at this container are
/// rejected by <see cref="IClientInventoryService.CanAnchorItemAt"/>.
/// </summary>
public sealed record EquipmentLayout(byte Window, IReadOnlyList<string> SlotNames) : ContainerLayout(Window)
{
    public int Capacity => SlotNames.Count;
}
