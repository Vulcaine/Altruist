/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using MessagePack;

namespace Altruist.Gaming.Inventory;

public static class InventoryPacketCodes
{
    public const uint MoveItem = 2000;
    public const uint PickupItem = 2001;
    public const uint DropItem = 2002;
    public const uint EquipItem = 2003;
    public const uint UnequipItem = 2004;
    public const uint UseItem = 2005;
    public const uint SortInventory = 2006;

    // Server → Client
    public const uint SlotUpdate = 2010;
    public const uint ItemResult = 2011;
}

[MessagePackObject]
public class MoveItemPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.MoveItem;
    [Key(1)] public SlotKey FromSlot { get; set; }
    [Key(2)] public SlotKey ToSlot { get; set; }
    [Key(3)] public short Count { get; set; } = 1;
}

[MessagePackObject]
public class PickupItemPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.PickupItem;
    [Key(1)] public string ItemInstanceId { get; set; } = "";
}

[MessagePackObject]
public class DropItemPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.DropItem;
    [Key(1)] public SlotKey FromSlot { get; set; }
}

[MessagePackObject]
public class EquipItemPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.EquipItem;
    [Key(1)] public SlotKey FromSlot { get; set; }
    [Key(2)] public string EquipSlotName { get; set; } = "";
}

[MessagePackObject]
public class UnequipItemPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.UnequipItem;
    [Key(1)] public string EquipSlotName { get; set; } = "";
}

[MessagePackObject]
public class UseItemPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.UseItem;
    [Key(1)] public SlotKey Slot { get; set; }
}

[MessagePackObject]
public class SortInventoryPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.SortInventory;
    [Key(1)] public string ContainerId { get; set; } = "inventory";
}

// Server → Client packets

[MessagePackObject]
public class SlotUpdatePacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.SlotUpdate;
    [Key(1)] public SlotKey Slot { get; set; }
    [Key(2)] public string ItemInstanceId { get; set; } = "";
    [Key(3)] public long ItemTemplateId { get; set; }
    [Key(4)] public short ItemCount { get; set; }
}

[MessagePackObject]
public class ItemResultPacket : IPacketBase
{
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.ItemResult;
    [Key(1)] public int Status { get; set; }
    [Key(2)] public string Message { get; set; } = "";
}
