/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Combat;

/// <summary>
/// Pluggable damage calculator. Games inject their own formula.
/// Default: attackPower - defensePower, minimum 1.
/// </summary>
public interface IDamageCalculator
{
    int Calculate(ICombatEntity attacker, ICombatEntity target);
}

/// <summary>
/// Core combat service. Handles hit detection, damage application, AoE sweeps.
/// Subscribe to events for game-specific reactions (XP, loot, aggro, animations).
/// </summary>
public interface ICombatService
{
    /// <summary>Single target attack using the registered IDamageCalculator.</summary>
    HitResult Attack(ICombatEntity attacker, ICombatEntity target);

    /// <summary>AoE sweep — finds all ICombatEntity in range, applies damage, returns all hits.</summary>
    SweepResult Sweep(ICombatEntity attacker, SweepQuery query, int? damage = null);

    /// <summary>Apply raw damage directly (bypasses calculator). Used by skills, DoTs, environment.</summary>
    HitResult ApplyDamage(ICombatEntity source, ICombatEntity target, int damage, DamageFlags flags = DamageFlags.Normal);

    /// <summary>Kill an entity immediately.</summary>
    void Kill(ICombatEntity entity, ICombatEntity? killer = null);

    /// <summary>Fired per entity hit (single target or per-entity in AoE).</summary>
    event Action<HitEvent>? OnHit;

    /// <summary>Fired when any entity dies from combat.</summary>
    event Action<DeathEvent>? OnDeath;

    /// <summary>Fired after an AoE sweep completes with all hit results.</summary>
    event Action<SweepEvent>? OnSweep;
}
