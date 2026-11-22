using MessagePack;

namespace Altruist.Client.Inventory.Packets;

/// <summary>
/// Wire format for a single inventory slot update. Generic across all games:
/// the only game-specific fields ride in <see cref="OpaquePayload"/>, which
/// the mirror passes through untouched.
///
/// <para><see cref="Window"/> is the consumer-defined container index — the
/// framework imposes nothing about what each value means.
/// <see cref="Cell"/> is <c>Y*Columns+X</c> for grids, or the
/// named-slot index for equipment. The mirror computes coverage from
/// <see cref="Width"/>×<see cref="Height"/>, so multi-cell items only need a
/// single anchor packet on the wire.</para>
///
/// <para>Sent by the server, consumed by <see cref="IClientInventoryService.OnItemSnapshot"/>.
/// The server must send the same MessagePack shape (keys 0..8) with
/// <see cref="ClientInventoryPacketCodes.ItemSnapshot"/>.</para>
/// </summary>
[MessagePackObject]
public sealed class ItemSnapshotPacket : IPacketBase
{
    /// <summary>Always <see cref="ClientInventoryPacketCodes.ItemSnapshot"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = ClientInventoryPacketCodes.ItemSnapshot;
    /// <summary>Container id.</summary>
    [Key(1)] public byte Window { get; set; }
    /// <summary>Anchor cell index.</summary>
    [Key(2)] public ushort Cell { get; set; }
    /// <summary>Item identifier; empty clears the slot.</summary>
    [Key(3)] public string ItemKey { get; set; } = string.Empty;
    /// <summary>Stack size; 0 or negative clears the slot.</summary>
    [Key(4)] public short Count { get; set; }
    /// <summary>Footprint width in cells; 0 is treated as 1.</summary>
    [Key(5)] public byte Width { get; set; } = 1;
    /// <summary>Footprint height in cells; 0 is treated as 1.</summary>
    [Key(6)] public byte Height { get; set; } = 1;
    /// <summary>Whether the item stacks.</summary>
    [Key(7)] public bool Stackable { get; set; }
    /// <summary>Game-specific bytes, passed through to <see cref="ItemSnapshot.OpaquePayload"/>.</summary>
    [Key(8)] public byte[] OpaquePayload { get; set; } = Array.Empty<byte>();
}

/// <summary>
/// Explicit slot-clear packet. Equivalent to an
/// <see cref="ItemSnapshotPacket"/> with an empty <see cref="ItemSnapshotPacket.ItemKey"/>,
/// but lets servers signal clears without having to populate the size fields.
/// </summary>
[MessagePackObject]
public sealed class ItemSlotClearedPacket : IPacketBase
{
    /// <summary>Always <see cref="ClientInventoryPacketCodes.ItemSlotCleared"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = ClientInventoryPacketCodes.ItemSlotCleared;
    /// <summary>Container id.</summary>
    [Key(1)] public byte Window { get; set; }
    /// <summary>Anchor cell to clear (linked cells are ignored).</summary>
    [Key(2)] public ushort Cell { get; set; }
}
