namespace Altruist.Client.Inventory;

/// <summary>
/// Canonical, game-agnostic view of a single inventory slot. The mirror cache
/// stores these and emits them in <see cref="Events.SlotChangedEvent"/>.
///
/// <para>Game-specific item metadata (refine level, sockets, anti-flag, etc.)
/// rides in <see cref="OpaquePayload"/> as an opaque byte array — Altruist
/// never inspects it. The consumer (game client) packs and unpacks this blob
/// however it wants. Keeping it opaque is what lets this module stay reusable
/// across different games with different item models.</para>
///
/// <para>Empty slots are represented by snapshots with an empty
/// <see cref="ItemKey"/> or zero <see cref="Count"/>. The mirror prefers
/// <see cref="Events.SlotClearedEvent"/> for explicit clears, but accepts
/// either shape on the wire.</para>
/// </summary>
/// <param name="Window">Container id.</param>
/// <param name="Cell">Anchor (top-left) cell index.</param>
/// <param name="ItemKey">Consumer-defined item identifier; empty means no item.</param>
/// <param name="Count">Stack size.</param>
/// <param name="Width">Footprint width in cells (at least 1).</param>
/// <param name="Height">Footprint height in cells (at least 1).</param>
/// <param name="Stackable">Whether the item stacks (informational; not used by the mirror).</param>
/// <param name="OpaquePayload">Game-specific bytes passed through untouched.</param>
public sealed record ItemSnapshot(
    byte Window,
    ushort Cell,
    string ItemKey,
    short Count,
    byte Width,
    byte Height,
    bool Stackable,
    byte[] OpaquePayload)
{
    /// <summary>True when <see cref="ItemKey"/> is empty or <see cref="Count"/> is 0.</summary>
    public bool IsEmpty => string.IsNullOrEmpty(ItemKey) || Count == 0;

    /// <summary>Creates an empty 1×1 snapshot for the given slot.</summary>
    /// <param name="window">Container id.</param>
    /// <param name="cell">Cell index.</param>
    /// <returns>A snapshot whose <see cref="IsEmpty"/> is <c>true</c>.</returns>
    public static ItemSnapshot Empty(byte window, ushort cell)
        => new(window, cell, string.Empty, 0, 1, 1, false, Array.Empty<byte>());
}
