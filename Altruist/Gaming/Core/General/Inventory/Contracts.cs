namespace Altruist.Gaming;



public interface IItemStoreService
{
    ItemStorageProvider CreateStorage(IStoragePrincipal principal, string storageId, (short Width, short Height) size, short slotCapacity);

    Task<ItemStorageProvider?> FindStorageAsync(string storageId);

    Task<SwapSlotStatus> SwapSlotsAsync(SlotKey from, SlotKey to);

    Task<SetItemStatus> SetItemAsync(
        SlotKey slotKey,
        string itemId,
        short itemCount
    );

    Task<(T? Item, MoveItemStatus Status)> MoveItemAsync<T>(
        string itemId,
        SlotKey fromSlotKey,
        SlotKey toSlotKey,
        short count = 1
    ) where T : GameItem;

    Task<(T? Item, RemoveItemStatus Status)> RemoveItemAsync<T>(
        SlotKey slotKey,
        short count = 1
    ) where T : GameItem;

    Task SortStorageAsync(string storageId);
}


/// <summary>
/// Represents an entity that owns or controls a storage.
/// This can be a player, world, guild, account, or any other system-defined principal.
/// </summary>
public interface IStoragePrincipal
{
    /// <summary>
    /// The unique identifier of the storage principal.
    /// </summary>
    string Id { get; init; }
}

/// <summary>
/// Abstract base record for defining storage principals.
/// Inherit from this record to define custom principals such as Player, World, Guild, etc.
/// </summary>
/// <param name="Id">The unique identifier of the storage principal.</param>
public abstract record StoragePrincipal(string Id) : IStoragePrincipal;

/// <summary>
/// Represents the world as a storage principal.
/// Typically used for global or environmental storage contexts.
/// </summary>
/// <param name="Id">The unique identifier of the world storage.</param>
public record WorldStoragePrincipal(string Id) : StoragePrincipal(Id);

/// <summary>
/// Represents a player as a storage principal.
/// Typically used for player inventories or personal storage.
/// </summary>
/// <param name="Id">The unique identifier of the player.</param>
public record PlayerStoragePrincipal(string Id) : StoragePrincipal(Id);

