/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Represents an entity that owns a container (player, world, guild, etc.).
/// Currently a typing aid only: <see cref="IInventoryService"/> addresses owners by plain string id
/// (<see cref="SlotKey.OwnerId"/>), so pass <see cref="Id"/> where an owner id is expected.
/// </summary>
public interface IStoragePrincipal
{
    /// <summary>Owner id used as <see cref="SlotKey.OwnerId"/>.</summary>
    string Id { get; }
}

/// <summary>A player owning containers (inventory, equipment, bank).</summary>
/// <param name="Id">Player id.</param>
public record PlayerPrincipal(string Id) : IStoragePrincipal;
/// <summary>A world instance owning ground items.</summary>
/// <param name="Id">World instance id.</param>
public record WorldPrincipal(string Id) : IStoragePrincipal;
/// <summary>A guild owning shared containers (e.g. a guild bank).</summary>
/// <param name="Id">Guild id.</param>
public record GuildPrincipal(string Id) : IStoragePrincipal;
