/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Combat;

/// <summary>
/// Any world object that can participate in combat (deal or receive damage).
/// Implement on your game's player, monster, NPC, destructible entities.
/// </summary>
public interface ICombatEntity
{
    uint VirtualId { get; }
    int Health { get; set; }
    int MaxHealth { get; }
    bool IsDead { get; }
    float X { get; }
    float Y { get; }
    float Z { get; }

    /// <summary>Attack power used by the default damage calculator. Override in your entity.</summary>
    virtual int GetAttackPower() => 0;

    /// <summary>Defense power used by the default damage calculator. Override in your entity.</summary>
    virtual int GetDefensePower() => 0;
}
