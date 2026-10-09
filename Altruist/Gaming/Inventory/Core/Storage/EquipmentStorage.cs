/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Equipment container with named slots (head, chest, weapon_main, etc.).
/// Each slot accepts specific item categories. Always capacity 1 (no stacking).
/// </summary>
/// <remarks>
/// When to use: worn/wielded items. Usually created by <see cref="IInventoryService.CreateContainer"/> with
/// <see cref="ContainerType.Equipment"/> and driven through <see cref="IInventoryService.EquipItemAsync"/> /
/// <see cref="IInventoryService.UnequipItemAsync"/>. The container id is always <c>"equipment"</c>.
/// Slots are addressed as (<see cref="EquipmentSlotDefinition.SlotIndex"/>, 0); prefer the name-based helpers
/// (<see cref="GetSlotByName"/>, <see cref="GetSlotKeyByName"/>). Only items with a non-null
/// <see cref="GameItem.EquipmentSlotType"/> are accepted (<see cref="ValidateItem"/>), and a slot accepts an item when
/// <see cref="GameItem.CanEquipTo"/> passes for its name and the category is allowed.
/// </remarks>
public class EquipmentStorage : IInventoryContainer
{
    private readonly Dictionary<short, StorageSlot> _slots = new();
    private readonly Dictionary<string, EquipmentSlotDefinition> _slotsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<short, EquipmentSlotDefinition> _slotsByIndex = new();

    /// <summary>Always <c>"equipment"</c>.</summary>
    public string ContainerId { get; }
    /// <inheritdoc/>
    public string OwnerId { get; }
    /// <summary>Always <see cref="ContainerType.Equipment"/>.</summary>
    public ContainerType ContainerType => ContainerType.Equipment;

    /// <summary>Creates one empty capacity-1 slot per definition. Prefer <see cref="IInventoryService.CreateContainer"/> so the service can find it.</summary>
    /// <param name="ownerId">Owner id.</param>
    /// <param name="slotDefinitions">Slot definitions; names and indices should be unique (later duplicates overwrite earlier ones).</param>
    public EquipmentStorage(string ownerId, IEnumerable<EquipmentSlotDefinition> slotDefinitions)
    {
        ContainerId = "equipment";
        OwnerId = ownerId;

        foreach (var def in slotDefinitions)
        {
            _slotsByName[def.SlotName] = def;
            _slotsByIndex[def.SlotIndex] = def;
            _slots[def.SlotIndex] = new StorageSlot
            {
                SlotKey = new SlotKey(def.SlotIndex, 0, ContainerId, ownerId),
                MaxCapacity = 1
            };
        }
    }

    /// <inheritdoc/>
    /// <remarks><paramref name="y"/> is ignored; <paramref name="x"/> is the slot index.</remarks>
    public StorageSlot? GetSlot(short x, short y)
        => _slots.TryGetValue(x, out var slot) ? slot : null;

    /// <summary>Returns the slot for a named equipment slot (case-insensitive).</summary>
    /// <param name="slotName">Slot name, e.g. <c>"head"</c>.</param>
    /// <returns>The slot, or null for an unknown name.</returns>
    public StorageSlot? GetSlotByName(string slotName)
    {
        if (_slotsByName.TryGetValue(slotName, out var def))
            return GetSlot(def.SlotIndex, 0);
        return null;
    }

    /// <summary>Returns the definition (index, accepted categories) of a named slot (case-insensitive).</summary>
    /// <param name="slotName">Slot name.</param>
    /// <returns>The definition, or null for an unknown name.</returns>
    public EquipmentSlotDefinition? GetSlotDefinition(string slotName)
        => _slotsByName.TryGetValue(slotName, out var def) ? def : null;

    /// <summary>Builds the <see cref="SlotKey"/> of a named slot, for use with <see cref="IInventoryService.MoveItemAsync"/> or <see cref="IInventoryService.SwapItemsAsync"/>.</summary>
    /// <param name="slotName">Slot name (case-insensitive).</param>
    /// <returns>The key, or <c>default(SlotKey)</c> for an unknown name (compare with <c>default</c>).</returns>
    public SlotKey GetSlotKeyByName(string slotName)
    {
        if (_slotsByName.TryGetValue(slotName, out var def))
            return new SlotKey(def.SlotIndex, 0, ContainerId, OwnerId);
        return default;
    }

    /// <inheritdoc/>
    public IReadOnlyCollection<StorageSlot> GetAllSlots() => _slots.Values.ToList();

    /// <inheritdoc/>
    public IEnumerable<StorageSlot> GetOccupiedSlots() => _slots.Values.Where(s => !s.IsEmpty);

    /// <inheritdoc/>
    public StorageSlot? FindItemSlot(string itemInstanceId)
        => _slots.Values.FirstOrDefault(s => s.ItemInstanceId == itemInstanceId);

    /// <summary>Accepts only equippable items (<see cref="GameItem.EquipmentSlotType"/> is not null).</summary>
    /// <param name="item">Item to test.</param>
    /// <returns>True when the item is equippable.</returns>
    public bool ValidateItem(GameItem item)
    {
        // Equipment slots require equippable items
        return item.EquipmentSlotType != null;
    }

    /// <summary>True when slot index <paramref name="x"/> exists, is empty, <see cref="GameItem.CanEquipTo"/> accepts its name, and the item's category is allowed.</summary>
    /// <param name="item">Item to test.</param>
    /// <param name="x">Slot index.</param>
    /// <param name="y">Ignored.</param>
    /// <param name="count">Ignored (equipment slots hold exactly one item).</param>
    /// <returns>True when the item can be equipped there.</returns>
    public bool CanFit(GameItem item, short x, short y, short count)
    {
        var slot = GetSlot(x, y);
        if (slot == null) return false;

        if (!_slotsByIndex.TryGetValue(x, out var def)) return false;

        // Check item is equippable to this slot
        if (!item.CanEquipTo(def.SlotName)) return false;

        // Check category restrictions
        if (def.AcceptedCategories.Length > 0 &&
            !def.AcceptedCategories.Contains(item.Category, StringComparer.OrdinalIgnoreCase))
            return false;

        return slot.IsEmpty; // Equipment slots don't stack
    }

    /// <summary>Equips the item into slot index <paramref name="x"/> with count 1 (no stacking).</summary>
    /// <param name="item">Item to equip.</param>
    /// <param name="x">Slot index.</param>
    /// <param name="y">Ignored.</param>
    /// <param name="count">Ignored; the slot always stores 1.</param>
    /// <returns><see cref="ItemStatus.Success"/>, <see cref="ItemStatus.InvalidSlot"/>, <see cref="ItemStatus.NotEnoughSpace"/> (occupied) or <see cref="ItemStatus.IncompatibleSlot"/>.</returns>
    public ItemStatus TryPlace(GameItem item, short x, short y, short count)
    {
        if (!CanFit(item, x, y, count))
        {
            var slot = GetSlot(x, y);
            if (slot == null) return ItemStatus.InvalidSlot;
            if (!slot.IsEmpty) return ItemStatus.NotEnoughSpace; // Slot occupied
            return ItemStatus.IncompatibleSlot;
        }

        var target = _slots[x];
        target.Set(item.InstanceId, item.TemplateId, 1);
        return ItemStatus.Success;
    }

    /// <summary>Equips into the first compatible empty slot (definition order).</summary>
    /// <param name="item">Item to equip.</param>
    /// <param name="count">Ignored.</param>
    /// <returns><see cref="ItemStatus.Success"/>, <see cref="ItemStatus.IncompatibleSlot"/> (not equippable) or <see cref="ItemStatus.NotEnoughSpace"/>.</returns>
    public ItemStatus TryPlaceAuto(GameItem item, short count)
    {
        if (item.EquipmentSlotType == null)
            return ItemStatus.IncompatibleSlot;

        // Find first compatible empty slot
        foreach (var (index, def) in _slotsByIndex)
        {
            if (item.CanEquipTo(def.SlotName) &&
                (def.AcceptedCategories.Length == 0 ||
                 def.AcceptedCategories.Contains(item.Category, StringComparer.OrdinalIgnoreCase)) &&
                _slots[index].IsEmpty)
            {
                return TryPlace(item, index, 0, count);
            }
        }

        return ItemStatus.NotEnoughSpace;
    }

    /// <summary>Clears the slot regardless of <paramref name="count"/>.</summary>
    /// <param name="x">Slot index.</param>
    /// <param name="y">Ignored.</param>
    /// <param name="count">Ignored.</param>
    /// <returns><see cref="ItemStatus.Success"/>, <see cref="ItemStatus.InvalidSlot"/> or <see cref="ItemStatus.ItemNotFound"/>.</returns>
    public ItemStatus Remove(short x, short y, short count)
    {
        var slot = GetSlot(x, y);
        if (slot == null) return ItemStatus.InvalidSlot;
        if (slot.IsEmpty) return ItemStatus.ItemNotFound;

        slot.Clear();
        return ItemStatus.Success;
    }
}
