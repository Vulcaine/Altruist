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
/// for unregistered windows are silently dropped — the consumer is
/// expected to declare its layouts up front.</para>
///
/// <para>Lifetime: registered as a DI singleton via
/// <see cref="ClientInventoryServiceConfig"/>. Resolve via
/// <c>Dependencies.Inject&lt;IClientInventoryService&gt;()</c> from any
/// consumer (Unity MonoBehaviour, server-side bot, headless test). The concrete
/// <see cref="ClientInventoryService"/> is only registered (and wired to the dispatcher)
/// when <c>altruist:client:transport</c> is configured; outside DI, construct
/// <see cref="ClientInventoryService"/> and pass it to
/// <see cref="ClientPacketDispatcher.Register"/>.</para>
///
/// <para>Threading: events fire synchronously on the thread that dispatches inbound
/// packets; marshal to your UI thread if needed. Queries are not synchronized against
/// concurrent updates.</para>
///
/// <para>Window ids and cell indices are consumer-defined; grid cells are
/// <c>y * columns + x</c> (see <see cref="GridLayout"/>).</para>
/// </summary>
/// <example>
/// <code>
/// var inventory = Dependencies.Inject&lt;IClientInventoryService&gt;();
/// inventory.RegisterContainer(new GridLayout(Window: 1, Columns: 5, Rows: 4));
/// inventory.RegisterContainer(new EquipmentLayout(Window: 2, SlotNames: new[] { "weapon", "head" }));
/// inventory.SlotChanged += e =&gt; ui.Draw(e.Snapshot);
/// inventory.SlotCleared += e =&gt; ui.Clear(e.Window, e.Cell);
///
/// if (inventory.CanAnchorItemAt(1, anchorCell: 7, width: 1, height: 3, ignoreAnchors: draggedFrom))
///     SendMoveRequest(...);
/// </code>
/// </example>
public interface IClientInventoryService
{
    /// <summary>
    /// Register a container layout. Idempotent: re-registering the same
    /// window replaces the layout AND clears the existing snapshot cache for
    /// that window (consumers should only call this at boot). No events fire for the
    /// dropped items.
    /// </summary>
    /// <param name="layout">A <see cref="GridLayout"/> or <see cref="EquipmentLayout"/>; its
    /// <see cref="ContainerLayout.Window"/> is the key.</param>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is <c>null</c>.</exception>
    void RegisterContainer(ContainerLayout layout);

    /// <summary>
    /// Direct anchor lookup — returns the snapshot if <paramref name="cell"/>
    /// is the anchor of an item, null otherwise. Use
    /// <see cref="GetItemCoveringCell"/> if you don't already know whether
    /// the cell is an anchor or a linked cell.
    /// </summary>
    /// <param name="window">Container id.</param>
    /// <param name="cell">Cell index.</param>
    /// <returns>The anchored item, or <c>null</c> (also for unknown windows).</returns>
    ItemSnapshot? GetSnapshot(byte window, ushort cell);

    /// <summary>
    /// Anchor-or-linked-cell lookup. For a 1×3 sword anchored at cell 0, this
    /// returns the same snapshot for cells 0, 5, and 10 (with a 5-wide grid).
    /// O(1) — backed by the per-container coverage map.
    /// </summary>
    /// <param name="window">Container id.</param>
    /// <param name="cell">Any cell of the item's footprint.</param>
    /// <returns>The covering item, or <c>null</c> (also for unknown windows).</returns>
    ItemSnapshot? GetItemCoveringCell(byte window, ushort cell);

    /// <summary>
    /// True if a W×H item can be placed with its top-left at
    /// <paramref name="anchorCell"/>. Cells whose anchor matches one of
    /// <paramref name="ignoreAnchors"/> are treated as free — needed for
    /// swap/move validation where the source's own footprint should be
    /// excluded from the collision check.
    /// </summary>
    /// <remarks>Read-only pre-check for UI (drag preview, move validation); the server stays
    /// authoritative. For grids the whole footprint must lie inside the grid. For equipment
    /// layouts only the anchor cell is checked and the size is ignored.</remarks>
    /// <param name="window">Container id.</param>
    /// <param name="anchorCell">Proposed top-left cell.</param>
    /// <param name="width">Item width in cells (must be 1..255).</param>
    /// <param name="height">Item height in cells (must be 1..255).</param>
    /// <param name="ignoreAnchors">Anchor cells whose items are treated as absent.</param>
    /// <returns><c>false</c> for unknown windows, non-positive sizes, out-of-bounds footprints or collisions.</returns>
    bool CanAnchorItemAt(byte window, ushort anchorCell, int width, int height, params ushort[] ignoreAnchors);

    /// <summary>
    /// Index of the first cell with no covering item, or -1 if the container
    /// is full (or unknown).
    /// </summary>
    /// <param name="window">Container id.</param>
    /// <returns>Lowest free cell index scanning from 0, or -1.</returns>
    int FindFirstFreeCell(byte window);

    /// <summary>All anchor snapshots for a container. Linked cells are not enumerated separately.</summary>
    /// <param name="window">Container id.</param>
    /// <returns>A live view of the cache (empty for unknown windows); copy it before mutating the
    /// inventory while enumerating.</returns>
    IReadOnlyCollection<ItemSnapshot> GetAllItems(byte window);

    /// <summary>
    /// Apply an inbound <see cref="ItemSnapshotPacket"/>. Normally the
    /// dispatcher calls this automatically via the <c>[Packet]</c>-decorated
    /// handler on the concrete implementation; consumers that translate a
    /// game-specific packet (e.g. Unity's <c>InventoryState</c> repacking
    /// <c>ItemSetEvent</c> → <c>ItemSnapshotPacket</c>) call it directly so
    /// the same code path updates the cache + fires <see cref="SlotChanged"/>.
    /// </summary>
    /// <remarks>Ignored for unregistered windows. An empty <c>ItemKey</c> or <c>Count &lt;= 0</c>
    /// clears the anchor (firing <see cref="SlotCleared"/> if something was there). A zero
    /// width/height is treated as 1. Out-of-bounds footprints are ignored without an event.</remarks>
    /// <param name="pkt">The slot update.</param>
    void OnItemSnapshot(ItemSnapshotPacket pkt);

    /// <summary>Apply an inbound <see cref="ItemSlotClearedPacket"/>. See
    /// <see cref="OnItemSnapshot"/> for the same direct-call rationale.</summary>
    /// <remarks>Fires <see cref="SlotCleared"/> only if an item was anchored at the cell;
    /// clearing a linked (non-anchor) cell is a no-op.</remarks>
    /// <param name="pkt">The slot clear.</param>
    void OnSlotCleared(ItemSlotClearedPacket pkt);

    /// <summary>Raised after an item is placed or updated at an anchor cell.</summary>
    event Action<SlotChangedEvent>? SlotChanged;
    /// <summary>Raised after an anchored item is removed (explicit clear or empty snapshot).</summary>
    event Action<SlotClearedEvent>? SlotCleared;
}
