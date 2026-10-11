/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using MessagePack;

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Addresses any slot in any container. The combination of <see cref="OwnerId"/> + <see cref="ContainerId"/>
/// identifies the storage instance (as registered in <see cref="IInventoryService"/>); <see cref="X"/> + <see cref="Y"/>
/// identifies the position within it.
/// </summary>
/// <remarks>
/// Coordinate conventions per container: <see cref="GridStorage"/> uses (column, row) with (0,0) top-left;
/// <see cref="SlotStorage"/> and <see cref="WorldItemStorage"/> use X as the flat slot index and Y = 0;
/// <see cref="EquipmentStorage"/> uses X = <see cref="EquipmentSlotDefinition.SlotIndex"/> and Y = 0
/// (prefer <see cref="EquipmentStorage.GetSlotKeyByName(string)"/> over hand-building equipment keys).
/// Use <see cref="Auto(string, string)"/> as a move destination to let the container pick the slot.
/// MessagePack-serializable, so it is sent directly inside the inventory packets.
/// Equality is ordinal on both strings plus X/Y.
/// </remarks>
/// <example>
/// <code>
/// var from = new SlotKey(3, 0, "inventory", playerId);
/// var to   = SlotKey.Auto("bank", playerId);
/// var result = await inventory.MoveItemAsync(from, to, count: 5);
/// </code>
/// </example>
[MessagePackObject]
public readonly struct SlotKey : IEquatable<SlotKey>
{
    /// <summary>Column index (or flat slot index for slot-based containers).</summary>
    [Key(0)] public short X { get; }

    /// <summary>Row index (always 0 for slot-based and equipment containers).</summary>
    [Key(1)] public short Y { get; }

    /// <summary>Container identifier: "inventory", "equipment", "bank", "world", etc.</summary>
    [Key(2)] public string ContainerId { get; }

    /// <summary>Owner identifier: player ID, world instance ID, guild ID, etc.</summary>
    [Key(3)] public string OwnerId { get; }

    /// <summary>Creates a key for a concrete position. Null <paramref name="containerId"/> / <paramref name="ownerId"/> become empty strings.</summary>
    /// <param name="x">Column (grid) or flat slot index (slot, equipment, world containers).</param>
    /// <param name="y">Row (grid); 0 for every other container type.</param>
    /// <param name="containerId">Container identifier, e.g. <c>"inventory"</c>, <c>"equipment"</c>, <c>"world"</c>.</param>
    /// <param name="ownerId">Owner identifier (player id, world instance id, ...).</param>
    public SlotKey(short x, short y, string containerId, string ownerId)
    {
        X = x;
        Y = y;
        ContainerId = containerId ?? "";
        OwnerId = ownerId ?? "";
    }

    /// <summary>
    /// Creates a SlotKey that signals "find first available slot" in the target container (X = Y = -1).
    /// Only meaningful as a destination: <see cref="IInventoryService.MoveItemAsync"/> and
    /// <see cref="IInventoryService.AddItem"/> route it to <see cref="IInventoryContainer.TryPlaceAuto"/>.
    /// Never use it as a source slot.
    /// </summary>
    /// <param name="containerId">Target container identifier.</param>
    /// <param name="ownerId">Owner of the target container.</param>
    public static SlotKey Auto(string containerId, string ownerId)
        => new(-1, -1, containerId, ownerId);

    /// <summary>True when this key is an auto-placement marker created by <see cref="Auto(string, string)"/> (X = Y = -1). Not serialized.</summary>
    [IgnoreMember] public bool IsAuto => X == -1 && Y == -1;

    /// <summary><c>"{OwnerId}:{ContainerId}"</c>; the same composite key <see cref="InventoryService"/> uses to index containers. Not serialized.</summary>
    [IgnoreMember] public string StorageKey => $"{OwnerId}:{ContainerId}";

    /// <summary>Value equality: same X, Y and ordinally equal container and owner ids.</summary>
    /// <param name="other">Key to compare with.</param>
    /// <returns>True when both keys address the same slot.</returns>
    public bool Equals(SlotKey other)
        => X == other.X && Y == other.Y &&
           string.Equals(ContainerId, other.ContainerId, StringComparison.Ordinal) &&
           string.Equals(OwnerId, other.OwnerId, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => obj is SlotKey other && Equals(other);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(X, Y, ContainerId, OwnerId);

    /// <summary>Value equality; see <see cref="Equals(SlotKey)"/>.</summary>
    public static bool operator ==(SlotKey left, SlotKey right) => left.Equals(right);
    /// <summary>Value inequality; see <see cref="Equals(SlotKey)"/>.</summary>
    public static bool operator !=(SlotKey left, SlotKey right) => !left.Equals(right);

    /// <summary>Debug form <c>[owner/container (x,y)]</c>.</summary>
    public override string ToString() => $"[{OwnerId}/{ContainerId} ({X},{Y})]";
}
