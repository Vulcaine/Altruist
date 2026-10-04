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
/// </summary>
[MessagePackObject]
public sealed class ItemSnapshotPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = ClientInventoryPacketCodes.ItemSnapshot;
    [Key(1)] public byte Window { get; set; }
    [Key(2)] public ushort Cell { get; set; }
    [Key(3)] public string ItemKey { get; set; } = string.Empty;
    [Key(4)] public short Count { get; set; }
    [Key(5)] public byte Width { get; set; } = 1;
    [Key(6)] public byte Height { get; set; } = 1;
    [Key(7)] public bool Stackable { get; set; }
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
    [Key(0)] public uint MessageCode { get; set; } = ClientInventoryPacketCodes.ItemSlotCleared;
    [Key(1)] public byte Window { get; set; }
    [Key(2)] public ushort Cell { get; set; }
}
