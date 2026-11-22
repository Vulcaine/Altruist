/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Combat;

/// <summary>
/// Any world object that can participate in combat (deal or receive damage).
/// Implement on your game's player, monster, NPC, destructible entities.
/// </summary>
/// <remarks>
/// <see cref="ICombatService.Sweep"/> only finds entities that are ALSO world objects in 3D world 0 of
/// <see cref="Altruist.Gaming.ThreeD.IGameWorldOrganizer3D"/>; single-target <see cref="ICombatService.Attack"/> /
/// <see cref="ICombatService.ApplyDamage"/> work on any implementation. Health is mutated directly by the service, so
/// keep it on the tick/world thread.
/// </remarks>
public interface ICombatEntity
{
    /// <summary>Stable per-entity id (the world object's virtual id); used to skip self-hits in sweeps and for lag-compensation history.</summary>
    uint VirtualId { get; }
    /// <summary>Current health. The combat service writes it (clamped at 0) when damage is applied or on kill.</summary>
    int Health { get; set; }
    /// <summary>Maximum health (used e.g. for HP percentage in <see cref="TargetInfoPacket"/>).</summary>
    int MaxHealth { get; }
    /// <summary>True when the entity can no longer be hit; dead targets are skipped and return a <see cref="DamageFlags.Miss"/> result.
    /// Typically <c>Health &lt;= 0</c>.</summary>
    bool IsDead { get; }
    /// <summary>World X position (same units as the world).</summary>
    float X { get; }
    /// <summary>World Y position (+Y up in 3D worlds).</summary>
    float Y { get; }
    /// <summary>World Z position.</summary>
    float Z { get; }

    /// <summary>Attack power used by the default damage calculator. Override in your entity.</summary>
    virtual int GetAttackPower() => 0;

    /// <summary>Defense power used by the default damage calculator. Override in your entity.</summary>
    virtual int GetDefensePower() => 0;
}
