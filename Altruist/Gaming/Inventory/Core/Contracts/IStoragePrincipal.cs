/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Inventory;

/// <summary>
/// Represents an entity that owns a container (player, world, guild, etc.).
/// </summary>
public interface IStoragePrincipal
{
    string Id { get; }
}

public record PlayerPrincipal(string Id) : IStoragePrincipal;
public record WorldPrincipal(string Id) : IStoragePrincipal;
public record GuildPrincipal(string Id) : IStoragePrincipal;
