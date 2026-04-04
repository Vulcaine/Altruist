/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

public enum ItemStatus
{
    Success = 0,
    NotEnoughSpace = 1,
    ItemNotFound = 2,
    StorageNotFound = 3,
    InvalidSlot = 4,
    NonStackable = 5,
    StackFull = 6,
    BadCount = 7,
    CannotMove = 8,
    IncompatibleSlot = 9,
    ItemExpired = 10,
    ValidationFailed = 11
}

public enum ContainerType
{
    Grid,
    Slot,
    Equipment
}

public record MoveItemResult(ItemStatus Status, GameItem? Item = null);
public record UseItemResult(ItemStatus Status, GameItem? Item = null);
