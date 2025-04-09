namespace Altruist.Gaming;

public enum ItemStatus
{
    Success = 0,
    NotEnoughSpace = 1,
    NonStackable = 2,
    ItemNotFound = 3,
    BadCount = 4,

    CannotMove = 5,

    StorageNotFound = 6
}


public enum AddItemStatus
{
    Success = ItemStatus.Success,
    NonStackable = ItemStatus.NonStackable,
    NotEnoughSpace = ItemStatus.NotEnoughSpace
}

public enum SetItemStatus
{
    Success = ItemStatus.Success,
    NonStackable = ItemStatus.NonStackable,
    ItemNotFound = ItemStatus.ItemNotFound,
    StorageNotFound = ItemStatus.StorageNotFound,
    NotEnoughSpace = ItemStatus.NotEnoughSpace,
}

public enum MoveItemStatus
{
    Success = ItemStatus.Success,
    NonStackable = ItemStatus.NonStackable,
    ItemNotFound = ItemStatus.ItemNotFound,
    NotEnoughSpace = ItemStatus.NotEnoughSpace,
    BadCount = ItemStatus.BadCount,
    CannotMove = ItemStatus.CannotMove,
    StorageNotFound = ItemStatus.StorageNotFound
}

public enum SwapSlotStatus
{
    Success = ItemStatus.Success,
    NonStackable = ItemStatus.NonStackable,
    ItemNotFound = ItemStatus.ItemNotFound,
    NotEnoughSpace = ItemStatus.NotEnoughSpace,
    BadCount = ItemStatus.BadCount,
    StorageNotFound = ItemStatus.StorageNotFound,
    CannotMove = ItemStatus.CannotMove,
}

public enum RemoveItemStatus
{
    Success = ItemStatus.Success,
    NonStackable = ItemStatus.NonStackable,
    ItemNotFound = ItemStatus.ItemNotFound,
    NotEnoughSpace = ItemStatus.NotEnoughSpace,
    BadCount = ItemStatus.BadCount,
    StorageNotFound = ItemStatus.StorageNotFound

}

public static class InventoryStatusCodeMessageMaping
{
    public static string GetMessage(this ItemStatus status) => status switch
    {
        ItemStatus.Success => "Success",
        ItemStatus.NotEnoughSpace => "Not enough space",
        ItemStatus.NonStackable => "Item is not stackable",
        ItemStatus.ItemNotFound => "Item not found",
        ItemStatus.BadCount => "Item count cannot be less than zero",
        ItemStatus.CannotMove => "Unable to move item",
        ItemStatus.StorageNotFound => "Storage not found",
        _ => throw new NotImplementedException(),
    };
}