/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using MessagePack;

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Wire message codes (<see cref="IPacketBase.MessageCode"/>) of the inventory packets, range 2000-2011.
/// Avoid reusing these values for your own packets.
/// </summary>
public static class InventoryPacketCodes
{
    /// <summary>Client to server: <see cref="MoveItemPacket"/>.</summary>
    public const uint MoveItem = 2000;
    /// <summary>Client to server: <see cref="PickupItemPacket"/>.</summary>
    public const uint PickupItem = 2001;
    /// <summary>Client to server: <see cref="DropItemPacket"/>.</summary>
    public const uint DropItem = 2002;
    /// <summary>Client to server: <see cref="EquipItemPacket"/>.</summary>
    public const uint EquipItem = 2003;
    /// <summary>Client to server: <see cref="UnequipItemPacket"/>.</summary>
    public const uint UnequipItem = 2004;
    /// <summary>Client to server: <see cref="UseItemPacket"/>.</summary>
    public const uint UseItem = 2005;
    /// <summary>Client to server: <see cref="SortInventoryPacket"/> (no built-in handler).</summary>
    public const uint SortInventory = 2006;

    // Server → Client
    /// <summary>Server to client: <see cref="SlotUpdatePacket"/>.</summary>
    public const uint SlotUpdate = 2010;
    /// <summary>Server to client: <see cref="ItemResultPacket"/>.</summary>
    public const uint ItemResult = 2011;
}

/// <summary>
/// Client request to move <see cref="Count"/> items between any two slots (gate <c>move-item</c>, handled by
/// <see cref="AltruistInventoryPortal.OnMoveItem"/>). Use <see cref="SlotKey.Auto(string, string)"/> as
/// <see cref="ToSlot"/> to let the destination container pick a slot.
/// </summary>
[MessagePackObject]
public class MoveItemPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="InventoryPacketCodes.MoveItem"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.MoveItem;
    /// <summary>Source slot (must hold an item).</summary>
    [Key(1)] public SlotKey FromSlot { get; set; }
    /// <summary>Destination slot, or an auto key.</summary>
    [Key(2)] public SlotKey ToSlot { get; set; }
    /// <summary>Number of items to move from the stack (default 1).</summary>
    [Key(3)] public short Count { get; set; } = 1;
}

/// <summary>
/// Client request to pick up a ground item into its <c>"inventory"</c> container (gate <c>pickup-item</c>,
/// handled by <see cref="AltruistInventoryPortal.OnPickupItem"/>).
/// </summary>
[MessagePackObject]
public class PickupItemPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="InventoryPacketCodes.PickupItem"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.PickupItem;
    /// <summary><see cref="GameItem.InstanceId"/> of the item lying in a world container.</summary>
    [Key(1)] public string ItemInstanceId { get; set; } = "";
}

/// <summary>
/// Client request to drop one item from a slot onto the ground (gate <c>drop-item</c>, handled by
/// <see cref="AltruistInventoryPortal.OnDropItem"/>).
/// </summary>
[MessagePackObject]
public class DropItemPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="InventoryPacketCodes.DropItem"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.DropItem;
    /// <summary>Slot to drop from.</summary>
    [Key(1)] public SlotKey FromSlot { get; set; }
}

/// <summary>
/// Client request to equip the item in <see cref="FromSlot"/> (gate <c>equip-item</c>, handled by
/// <see cref="AltruistInventoryPortal.OnEquipItem"/>).
/// </summary>
[MessagePackObject]
public class EquipItemPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="InventoryPacketCodes.EquipItem"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.EquipItem;
    /// <summary>Slot holding the item to equip.</summary>
    [Key(1)] public SlotKey FromSlot { get; set; }
    /// <summary>Target <see cref="EquipmentSlotDefinition.SlotName"/>; empty = first compatible empty slot. An occupied named slot is swapped.</summary>
    [Key(2)] public string EquipSlotName { get; set; } = "";
}

/// <summary>
/// Client request to move an equipped item back to its <c>"inventory"</c> container (gate <c>unequip-item</c>,
/// handled by <see cref="AltruistInventoryPortal.OnUnequipItem"/>).
/// </summary>
[MessagePackObject]
public class UnequipItemPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="InventoryPacketCodes.UnequipItem"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.UnequipItem;
    /// <summary><see cref="EquipmentSlotDefinition.SlotName"/> to clear (case-insensitive).</summary>
    [Key(1)] public string EquipSlotName { get; set; } = "";
}

/// <summary>
/// Client request to use the item in <see cref="Slot"/> (gate <c>use-item</c>, handled by
/// <see cref="AltruistInventoryPortal.OnUseItem"/>).
/// </summary>
[MessagePackObject]
public class UseItemPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="InventoryPacketCodes.UseItem"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.UseItem;
    /// <summary>Slot holding the item to use.</summary>
    [Key(1)] public SlotKey Slot { get; set; }
}

/// <summary>
/// Client request to sort a container. Defined for the protocol only: there is no built-in gate or service
/// operation, so add your own <c>[Gate]</c> handler if you use it.
/// </summary>
[MessagePackObject]
public class SortInventoryPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="InventoryPacketCodes.SortInventory"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.SortInventory;
    /// <summary>Container to sort (default <c>"inventory"</c>).</summary>
    [Key(1)] public string ContainerId { get; set; } = "inventory";
}

// Server → Client packets

/// <summary>
/// Server notification of one slot's new contents. Not sent by the built-in portal; send it from the
/// <c>On...Completed</c> hooks of <see cref="AltruistInventoryPortal"/> (or anywhere) when clients mirror container state.
/// An empty <see cref="ItemInstanceId"/> with count 0 means the slot is now empty.
/// </summary>
[MessagePackObject]
public class SlotUpdatePacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="InventoryPacketCodes.SlotUpdate"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.SlotUpdate;
    /// <summary>Slot that changed.</summary>
    [Key(1)] public SlotKey Slot { get; set; }
    /// <summary>Instance id now in the slot; empty when cleared.</summary>
    [Key(2)] public string ItemInstanceId { get; set; } = "";
    /// <summary>Template id now in the slot.</summary>
    [Key(3)] public long ItemTemplateId { get; set; }
    /// <summary>Stack count now in the slot (0 for linked grid cells and empty slots).</summary>
    [Key(4)] public short ItemCount { get; set; }
}

/// <summary>
/// Server reply to every inventory request handled by <see cref="AltruistInventoryPortal"/>.
/// </summary>
[MessagePackObject]
public class ItemResultPacket : IPacketBase
{
    /// <summary>Wire message code; defaults to <see cref="InventoryPacketCodes.ItemResult"/>.</summary>
    [Key(0)] public uint MessageCode { get; set; } = InventoryPacketCodes.ItemResult;
    /// <summary>Numeric <see cref="ItemStatus"/> value.</summary>
    [Key(1)] public int Status { get; set; }
    /// <summary>The <see cref="ItemStatus"/> name (e.g. <c>"NotEnoughSpace"</c>); informational.</summary>
    [Key(2)] public string Message { get; set; } = "";
}
