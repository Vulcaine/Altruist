/* 
Copyright 2025 Aron Gere

Licensed under the Apache License, Version 2.0 (the "License");
you may not use this file except in compliance with the License.
You may obtain a copy of the License at

    http://www.apache.org/licenses/LICENSE-2.0

Unless required by applicable law or agreed to in writing, software
distributed under the License is distributed on an "AS IS" BASIS,
WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
See the License for the specific language governing permissions and
limitations under the License.
*/

namespace Altruist.Gaming;

public interface IItemStoreService
{
    ItemStorageProvider CreateStorage(IStoragePrincipal principal, string storageId, (short Width, short Height) size, short slotCapacity);

    Task<ItemStorageProvider?> FindStorageAsync(string storageId);

    Task<SwapSlotStatus> SwapSlotsAsync(SlotKey from, SlotKey to);

    Task<T?> FindItemAsync<T>(string storageId, SlotKey key) where T : GameItem;

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

    Task<IEnumerable<StorageSlot>> SortStorageAsync(
        string storageId,
        Func<List<SlotGroup>, Task<List<SlotGroup>>> sortFunc);
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

