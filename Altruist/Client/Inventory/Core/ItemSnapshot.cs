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
    public bool IsEmpty => string.IsNullOrEmpty(ItemKey) || Count == 0;

    public static ItemSnapshot Empty(byte window, ushort cell)
        => new(window, cell, string.Empty, 0, 1, 1, false, Array.Empty<byte>());
}
