/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using MessagePack;

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Addresses any slot in any container. The combination of OwnerId + ContainerId
/// identifies the storage instance; X + Y identifies the position within it.
/// </summary>
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

    public SlotKey(short x, short y, string containerId, string ownerId)
    {
        X = x;
        Y = y;
        ContainerId = containerId ?? "";
        OwnerId = ownerId ?? "";
    }

    /// <summary>
    /// Creates a SlotKey that signals "find first available slot" in the target container.
    /// </summary>
    public static SlotKey Auto(string containerId, string ownerId)
        => new(-1, -1, containerId, ownerId);

    [IgnoreMember] public bool IsAuto => X == -1 && Y == -1;

    [IgnoreMember] public string StorageKey => $"{OwnerId}:{ContainerId}";

    public bool Equals(SlotKey other)
        => X == other.X && Y == other.Y &&
           string.Equals(ContainerId, other.ContainerId, StringComparison.Ordinal) &&
           string.Equals(OwnerId, other.OwnerId, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SlotKey other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(X, Y, ContainerId, OwnerId);

    public static bool operator ==(SlotKey left, SlotKey right) => left.Equals(right);
    public static bool operator !=(SlotKey left, SlotKey right) => !left.Equals(right);

    public override string ToString() => $"[{OwnerId}/{ContainerId} ({X},{Y})]";
}
