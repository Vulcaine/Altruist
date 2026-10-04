using Altruist.Client.Inventory.Events;
using Altruist.Client.Inventory.Packets;

namespace Altruist.Client.Inventory;

/// <summary>
/// Client-side inventory mirror. Subscribes to <see cref="Packets.ItemSnapshotPacket"/>
/// + <see cref="Packets.ItemSlotClearedPacket"/> on the dispatcher,
/// maintains per-container snapshot cache with multi-cell coverage, and
/// surfaces <see cref="SlotChanged"/> / <see cref="SlotCleared"/> events for
/// UI consumers.
///
/// <para>The mirror is dimension-less until the consumer registers
/// containers via <see cref="RegisterContainer"/> at boot. Snapshots arriving
/// for unregistered windows are dropped with a diagnostic — the consumer is
/// expected to declare its layouts up front.</para>
///
/// <para>Lifetime: registered as a DI singleton via
/// <see cref="ClientInventoryServiceConfig"/>. Resolve via
/// <c>Dependencies.Inject&lt;IClientInventoryService&gt;()</c> from any
/// consumer (Unity MonoBehaviour, server-side bot, headless test).</para>
/// </summary>
public interface IClientInventoryService
{
    /// <summary>
    /// Register a container layout. Idempotent: re-registering the same
    /// window replaces the layout AND clears the existing snapshot cache for
    /// that window (consumers should only call this at boot).
    /// </summary>
    void RegisterContainer(ContainerLayout layout);

    /// <summary>
    /// Direct anchor lookup — returns the snapshot if <paramref name="cell"/>
    /// is the anchor of an item, null otherwise. Use
    /// <see cref="GetItemCoveringCell"/> if you don't already know whether
    /// the cell is an anchor or a linked cell.
    /// </summary>
    ItemSnapshot? GetSnapshot(byte window, ushort cell);

    /// <summary>
    /// Anchor-or-linked-cell lookup. For a 1×3 sword anchored at cell 0, this
    /// returns the same snapshot for cells 0, 5, and 10 (with a 5-wide grid).
    /// O(1) — backed by the per-container coverage map.
    /// </summary>
    ItemSnapshot? GetItemCoveringCell(byte window, ushort cell);

    /// <summary>
    /// True if a W×H item can be placed with its top-left at
    /// <paramref name="anchorCell"/>. Cells whose anchor matches one of
    /// <paramref name="ignoreAnchors"/> are treated as free — needed for
    /// swap/move validation where the source's own footprint should be
    /// excluded from the collision check.
    /// </summary>
    bool CanAnchorItemAt(byte window, ushort anchorCell, int width, int height, params ushort[] ignoreAnchors);

    /// <summary>
    /// Index of the first cell with no covering item, or -1 if the container
    /// is full (or unknown).
    /// </summary>
    int FindFirstFreeCell(byte window);

    /// <summary>All anchor snapshots for a container. Linked cells are not enumerated separately.</summary>
    IReadOnlyCollection<ItemSnapshot> GetAllItems(byte window);

    /// <summary>
    /// Apply an inbound <see cref="ItemSnapshotPacket"/>. Normally the
    /// dispatcher calls this automatically via the <c>[Packet]</c>-decorated
    /// handler on the concrete implementation; consumers that translate a
    /// game-specific packet (e.g. Unity's <c>InventoryState</c> repacking
    /// <c>ItemSetEvent</c> → <c>ItemSnapshotPacket</c>) call it directly so
    /// the same code path updates the cache + fires <see cref="SlotChanged"/>.
    /// </summary>
    void OnItemSnapshot(ItemSnapshotPacket pkt);

    /// <summary>Apply an inbound <see cref="ItemSlotClearedPacket"/>. See
    /// <see cref="OnItemSnapshot"/> for the same direct-call rationale.</summary>
    void OnSlotCleared(ItemSlotClearedPacket pkt);

    event Action<SlotChangedEvent>? SlotChanged;
    event Action<SlotClearedEvent>? SlotCleared;
}
