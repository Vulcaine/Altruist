using System.Collections.Concurrent;
using Altruist.Client.Inventory.Events;
using Altruist.Client.Inventory.Packets;

namespace Altruist.Client.Inventory;

/// <summary>
/// Default <see cref="IClientInventoryService"/>. Subscribes to inbound
/// <see cref="ItemSnapshotPacket"/> + <see cref="ItemSlotClearedPacket"/>
/// frames on the dispatcher and maintains a per-window
/// <see cref="ContainerSnapshot"/>.
///
/// <para>The mirror is registered as a singleton (one cache per client
/// process) and discovered automatically by
/// <see cref="ClientPacketHandlerConfig"/> via the <see cref="PacketHandlerAttribute"/>
/// marker. No manual wiring required from the consumer beyond calling
/// <see cref="RegisterContainer"/> at boot.</para>
///
/// <para>Layouts are kept in a concurrent dictionary so a worker thread
/// could call <see cref="RegisterContainer"/> safely, but the mutation
/// methods (handler dispatch + clear) assume single-threaded access — the
/// dispatcher drains inbound on one thread.</para>
///
/// <para>Depend on <see cref="IClientInventoryService"/>; use this concrete type only
/// when constructing the mirror manually (tests, non-DI clients).</para>
/// </summary>
[PacketHandler]
public sealed class ClientInventoryService : IClientInventoryService
{
    private readonly ConcurrentDictionary<byte, ContainerSnapshot> _containers = new();

    /// <inheritdoc/>
    public event Action<SlotChangedEvent>? SlotChanged;
    /// <inheritdoc/>
    public event Action<SlotClearedEvent>? SlotCleared;

    /// <inheritdoc/>
    public void RegisterContainer(ContainerLayout layout)
    {
        if (layout is null) throw new ArgumentNullException(nameof(layout));
        // Replace policy: re-registration drops the existing cache. Containers
        // are expected to be registered exactly once at boot; a re-register
        // implies the consumer is reconfiguring (e.g. after reconnect with a
        // changed ruleset) and stale data would be misleading.
        _containers[layout.Window] = new ContainerSnapshot(layout);
    }

    /// <inheritdoc/>
    public ItemSnapshot? GetSnapshot(byte window, ushort cell)
        => _containers.TryGetValue(window, out var c) ? c.GetAnchor(cell) : null;

    /// <inheritdoc/>
    public ItemSnapshot? GetItemCoveringCell(byte window, ushort cell)
        => _containers.TryGetValue(window, out var c) ? c.GetItemCoveringCell(cell) : null;

    /// <inheritdoc/>
    public bool CanAnchorItemAt(byte window, ushort anchorCell, int width, int height, params ushort[] ignoreAnchors)
    {
        if (!_containers.TryGetValue(window, out var c)) return false;
        return c.CanAnchorItemAt(anchorCell, width, height, ignoreAnchors);
    }

    /// <inheritdoc/>
    public int FindFirstFreeCell(byte window)
        => _containers.TryGetValue(window, out var c) ? c.FindFirstFreeCell() : -1;

    /// <inheritdoc/>
    public IReadOnlyCollection<ItemSnapshot> GetAllItems(byte window)
        => _containers.TryGetValue(window, out var c)
            ? c.Anchors
            : Array.Empty<ItemSnapshot>();

    /// <inheritdoc/>
    [Packet(typeof(ItemSnapshotPacket))]
    public void OnItemSnapshot(ItemSnapshotPacket pkt)
    {
        if (!_containers.TryGetValue(pkt.Window, out var c)) return;

        // Empty snapshots are equivalent to an explicit clear.
        if (string.IsNullOrEmpty(pkt.ItemKey) || pkt.Count <= 0)
        {
            if (c.ClearAnchor(pkt.Cell))
                SlotCleared?.Invoke(new SlotClearedEvent(pkt.Window, pkt.Cell));
            return;
        }

        var snap = new ItemSnapshot(
            pkt.Window,
            pkt.Cell,
            pkt.ItemKey,
            pkt.Count,
            pkt.Width == 0 ? (byte)1 : pkt.Width,
            pkt.Height == 0 ? (byte)1 : pkt.Height,
            pkt.Stackable,
            pkt.OpaquePayload ?? Array.Empty<byte>());

        if (!c.ApplySnapshot(snap)) return;
        SlotChanged?.Invoke(new SlotChangedEvent(snap));
    }

    /// <inheritdoc/>
    [Packet(typeof(ItemSlotClearedPacket))]
    public void OnSlotCleared(ItemSlotClearedPacket pkt)
    {
        if (!_containers.TryGetValue(pkt.Window, out var c)) return;
        if (c.ClearAnchor(pkt.Cell))
            SlotCleared?.Invoke(new SlotClearedEvent(pkt.Window, pkt.Cell));
    }
}
