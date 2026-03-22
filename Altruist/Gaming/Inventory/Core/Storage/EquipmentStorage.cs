/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Equipment container with named slots (head, chest, weapon_main, etc.).
/// Each slot accepts specific item categories. Always capacity 1 (no stacking).
/// </summary>
public class EquipmentStorage : IInventoryContainer
{
    private readonly Dictionary<short, StorageSlot> _slots = new();
    private readonly Dictionary<string, EquipmentSlotDefinition> _slotsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<short, EquipmentSlotDefinition> _slotsByIndex = new();

    public string ContainerId { get; }
    public string OwnerId { get; }
    public ContainerType ContainerType => ContainerType.Equipment;

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

    public StorageSlot? GetSlot(short x, short y)
        => _slots.TryGetValue(x, out var slot) ? slot : null;

    public StorageSlot? GetSlotByName(string slotName)
    {
        if (_slotsByName.TryGetValue(slotName, out var def))
            return GetSlot(def.SlotIndex, 0);
        return null;
    }

    public EquipmentSlotDefinition? GetSlotDefinition(string slotName)
        => _slotsByName.TryGetValue(slotName, out var def) ? def : null;

    public SlotKey GetSlotKeyByName(string slotName)
    {
        if (_slotsByName.TryGetValue(slotName, out var def))
            return new SlotKey(def.SlotIndex, 0, ContainerId, OwnerId);
        return default;
    }

    public IReadOnlyCollection<StorageSlot> GetAllSlots() => _slots.Values.ToList();

    public IEnumerable<StorageSlot> GetOccupiedSlots() => _slots.Values.Where(s => !s.IsEmpty);

    public StorageSlot? FindItemSlot(string itemInstanceId)
        => _slots.Values.FirstOrDefault(s => s.ItemInstanceId == itemInstanceId);

    public bool ValidateItem(GameItem item)
    {
        // Equipment slots require equippable items
        return item.EquipmentSlotType != null;
    }

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

    public ItemStatus Remove(short x, short y, short count)
    {
        var slot = GetSlot(x, y);
        if (slot == null) return ItemStatus.InvalidSlot;
        if (slot.IsEmpty) return ItemStatus.ItemNotFound;

        slot.Clear();
        return ItemStatus.Success;
    }
}
