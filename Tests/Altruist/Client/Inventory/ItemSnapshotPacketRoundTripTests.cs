using Altruist.Client;
using Altruist.Client.Inventory;
using Altruist.Client.Inventory.Packets;
using MessagePack;
using MessagePack.Resolvers;

namespace Tests.Altruist.Client.Inventory;

/// <summary>
/// MessagePack round-trip for the wire types. Pins the contract that game
/// fields packed into <see cref="ItemSnapshotPacket.OpaquePayload"/> survive
/// a full serialize/deserialize cycle byte-for-byte — that's the whole point
/// of keeping the field opaque, so a regression that strips or reorders it
/// would silently corrupt every game's item state.
/// </summary>
public sealed class ItemSnapshotPacketRoundTripTests
{
    private static readonly MessagePackSerializerOptions Options =
        MessagePackSerializerOptions.Standard.WithResolver(
            CompositeResolver.Create(
                StandardResolverAllowPrivate.Instance,
                TypelessContractlessStandardResolver.Instance));

    [Fact]
    public void ItemSnapshotPacket_RoundTrips_AllFields()
    {
        var original = new ItemSnapshotPacket
        {
            Window = 1,
            Cell = 17,
            ItemKey = "sword+5",
            Count = 1,
            Width = 1,
            Height = 3,
            Stackable = false,
            OpaquePayload = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE },
        };

        var bytes = MessagePackSerializer.Serialize(original, Options);
        var decoded = MessagePackSerializer.Deserialize<ItemSnapshotPacket>(bytes, Options);

        Assert.Equal(ClientInventoryPacketCodes.ItemSnapshot, decoded.MessageCode);
        Assert.Equal(original.Window, decoded.Window);
        Assert.Equal(original.Cell, decoded.Cell);
        Assert.Equal(original.ItemKey, decoded.ItemKey);
        Assert.Equal(original.Count, decoded.Count);
        Assert.Equal(original.Width, decoded.Width);
        Assert.Equal(original.Height, decoded.Height);
        Assert.Equal(original.Stackable, decoded.Stackable);
        Assert.Equal(original.OpaquePayload, decoded.OpaquePayload);
    }

    [Fact]
    public void OpaquePayload_BytesAreNeverInspected_AndPreservedExactly()
    {
        // Worst-case payload: leading/trailing zeroes, embedded MessagePack-ish bytes.
        // If anything in the framework tries to interpret this, it'd corrupt these.
        var noisyPayload = new byte[]
        {
            0x00, 0x00,
            0xFF, 0xFE, 0xFD,
            0x82, 0xA3, 0x66, 0x6F, 0x6F, 0x01, 0xA3, 0x62, 0x61, 0x72, 0x02, // {"foo":1,"bar":2}
            0x00,
        };

        var pkt = new ItemSnapshotPacket
        {
            Window = 2,
            Cell = 0,
            ItemKey = "monk_plate_armor",
            Count = 1,
            Width = 1,
            Height = 2,
            OpaquePayload = noisyPayload,
        };

        var roundTripped = MessagePackSerializer.Deserialize<ItemSnapshotPacket>(
            MessagePackSerializer.Serialize(pkt, Options), Options);

        Assert.Equal(noisyPayload, roundTripped.OpaquePayload);
    }

    [Fact]
    public void EmptyPacket_DefaultsAreSerialisable_AndMatchTheClearShape()
    {
        // An "empty slot" snapshot — Count=0 + ItemKey="" — must survive the wire
        // unchanged. ClientInventoryService treats this as an explicit clear, so
        // a default-constructed packet that's a no-op for the consumer must NOT
        // gain any unintended fields on round-trip.
        var pkt = new ItemSnapshotPacket { Window = 1, Cell = 5 };

        var decoded = MessagePackSerializer.Deserialize<ItemSnapshotPacket>(
            MessagePackSerializer.Serialize(pkt, Options), Options);

        Assert.Equal(string.Empty, decoded.ItemKey);
        Assert.Equal(0, decoded.Count);
        Assert.Equal(1, decoded.Width);
        Assert.Equal(1, decoded.Height);
        Assert.Empty(decoded.OpaquePayload);
        Assert.True(new ItemSnapshot(
            decoded.Window, decoded.Cell, decoded.ItemKey, decoded.Count,
            decoded.Width, decoded.Height, decoded.Stackable, decoded.OpaquePayload).IsEmpty);
    }

    [Fact]
    public void ItemSlotClearedPacket_RoundTrips()
    {
        var original = new ItemSlotClearedPacket { Window = 1, Cell = 42 };
        var decoded = MessagePackSerializer.Deserialize<ItemSlotClearedPacket>(
            MessagePackSerializer.Serialize(original, Options), Options);

        Assert.Equal(ClientInventoryPacketCodes.ItemSlotCleared, decoded.MessageCode);
        Assert.Equal(original.Window, decoded.Window);
        Assert.Equal(original.Cell, decoded.Cell);
    }
}
