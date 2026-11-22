/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Combat;

/// <summary>
/// Pluggable damage calculator. Games inject their own formula.
/// Default: attackPower - defensePower, minimum 1.
/// </summary>
/// <remarks>
/// Register your implementation with <c>[Service(typeof(IDamageCalculator))]</c>; <see cref="CombatService"/> picks it
/// up through DI and falls back to <see cref="DefaultDamageCalculator"/> when none is registered. Used only by
/// <see cref="ICombatService.Attack"/> and by <see cref="ICombatService.Sweep"/> when no fixed damage is given;
/// <see cref="ICombatService.ApplyDamage"/> bypasses it.
/// </remarks>
public interface IDamageCalculator
{
    /// <summary>Computes the damage and flags for <paramref name="attacker"/> hitting <paramref name="target"/>.
    /// Called on the combat caller's thread, possibly inside a lag-compensation rewind.</summary>
    /// <param name="attacker">Entity dealing the hit.</param>
    /// <param name="target">Entity receiving the hit (alive).</param>
    /// <returns>Damage to subtract from the target's health plus descriptive flags.</returns>
    DamageSpec Calculate(ICombatEntity attacker, ICombatEntity target);
}

/// <summary>
/// Core combat service. Handles hit detection, damage application, AoE sweeps.
/// Subscribe to events for game-specific reactions (XP, loot, aggro, animations).
/// </summary>
/// <remarks>
/// <para>
/// Health-based (MMO/RPG-style) combat for <see cref="ICombatEntity"/> objects. Registered as a singleton
/// (<see cref="CombatService"/>, <c>[Service(typeof(ICombatService))]</c>); inject it into portals, AI behaviours or
/// game services. Not thread-safe: call it from the world/tick thread or a gate handler, not concurrently.
/// </para>
/// <para>
/// Choosing: use this service for server-side damage rules (calculators, AoE sweeps, death events, lag compensation).
/// Use <see cref="AltruistCombatPortal"/> on top of it when clients send attack/target packets over the default
/// gates. For physics-driven games (knockback, impulses) use the physics/body APIs instead; this service only moves
/// health numbers.
/// </para>
/// <para>
/// Reactions: subscribe to <see cref="OnHit"/>/<see cref="OnDeath"/>/<see cref="OnSweep"/> for catch-all callbacks, or
/// write <see cref="CombatHandlerAttribute"/> classes for handlers typed per actor pair. Event order for a lethal hit:
/// <c>[CombatEvent]</c> handlers for <see cref="HitEvent"/>, then <see cref="OnHit"/>, then the same pair for
/// <see cref="DeathEvent"/>.
/// </para>
/// <para>
/// Lag compensation: when <see cref="Altruist.Gaming.ILagCompensationService"/> is registered and the current packet
/// carried a client tick (<see cref="Altruist.ILagCompensated"/> sets <c>PacketContext.ClientTick</c>),
/// <see cref="Attack"/> and <see cref="Sweep"/> transparently run inside a world rewind to that tick.
/// </para>
/// </remarks>
/// <example>
/// <code>
/// // Single target using the registered IDamageCalculator
/// var hit = combat.Attack(attacker, target);
///
/// // 90° frontal cone, 3 units long, fixed 25 damage, at most 5 targets, in the XZ ground plane
/// var cone = SweepQuery3D.Cone(attackerPos, attackerYaw, range: 3f, halfAngleRadians: MathF.PI / 4) with { MaxTargets = 5 };
/// var sweep = combat.Sweep(attacker, cone, damage: 25);
///
/// // Damage over time / environment: bypass the calculator
/// combat.ApplyDamage(source: trap, target: player, damage: 5);
/// </code>
/// </example>
public interface ICombatService
{
    /// <summary>Single target attack using the registered IDamageCalculator.</summary>
    /// <remarks>Lag-compensated when enabled (see the interface remarks). The calculator runs, then the result is
    /// applied exactly like <see cref="ApplyDamage"/> (events, kill on 0 health). Does no range/line-of-sight
    /// check: validate reach yourself.</remarks>
    /// <param name="attacker">Entity dealing the hit.</param>
    /// <param name="target">Entity receiving the hit.</param>
    /// <param name="context">Opaque data forwarded to <see cref="HitEvent.Context"/>.</param>
    /// <returns>The hit; damage 0 and <see cref="DamageFlags.Miss"/> when the target was already dead.</returns>
    HitResult Attack(ICombatEntity attacker, ICombatEntity target, object? context = null);

    /// <summary>AoE sweep — finds all ICombatEntity in range, applies damage, returns all hits.</summary>
    /// <remarks>
    /// Candidates are the <see cref="ICombatEntity"/> world objects of 3D world index 0 (nothing is found when no
    /// <see cref="Altruist.Gaming.ThreeD.IGameWorldOrganizer3D"/> or world 0 exists). The attacker (same
    /// <see cref="ICombatEntity.VirtualId"/>), dead entities and entities rejected by <see cref="SweepQuery3D.Filter"/>
    /// are skipped. Targets are hit in world snapshot order until <see cref="SweepQuery3D.MaxTargets"/> is reached,
    /// so the cap is not nearest-first. Raises a <see cref="HitEvent"/> per target, then one <see cref="SweepEvent"/>.
    /// Lag-compensated when enabled: candidate positions are rewound to the client tick.
    /// For 2D-shaped queries see <see cref="Altruist.Gaming.Combat.TwoD.SweepQuery2D"/> (no service consumes it yet).
    /// </remarks>
    /// <param name="attacker">Entity performing the sweep (excluded from the results).</param>
    /// <param name="query">Shape, plane and limits; build it with the <see cref="SweepQuery3D"/> factories.</param>
    /// <param name="damage">Fixed damage per target, or null to run <see cref="IDamageCalculator"/> per target.</param>
    /// <param name="flags">Flags for fixed damage (ignored when <paramref name="damage"/> is null).</param>
    /// <param name="context">Opaque data forwarded to each <see cref="HitEvent.Context"/>.</param>
    /// <returns>All hits in order.</returns>
    SweepResult Sweep(ICombatEntity attacker, SweepQuery3D query, int? damage = null, DamageFlags flags = DamageFlags.Normal, object? context = null);

    /// <summary>Apply raw damage directly (bypasses calculator). Used by skills, DoTs, environment.</summary>
    /// <remarks>Not lag-compensated. Health is clamped at 0; a <see cref="HitEvent"/> is raised, then
    /// <see cref="Kill"/> runs if health reached 0. Negative damage heals but is not capped at
    /// <see cref="ICombatEntity.MaxHealth"/>.</remarks>
    /// <param name="source">Entity credited with the damage (may be the target itself for self-damage).</param>
    /// <param name="target">Entity receiving the damage.</param>
    /// <param name="damage">Amount subtracted from <see cref="ICombatEntity.Health"/>.</param>
    /// <param name="flags">Descriptive flags carried on the events/result.</param>
    /// <param name="context">Opaque data forwarded to <see cref="HitEvent.Context"/>.</param>
    /// <returns>The hit; damage 0 and <see cref="DamageFlags.Miss"/> when the target was already dead.</returns>
    HitResult ApplyDamage(ICombatEntity source, ICombatEntity target, int damage, DamageFlags flags = DamageFlags.Normal, object? context = null);

    /// <summary>Kill an entity immediately.</summary>
    /// <remarks>Sets health to 0 and raises <see cref="DeathEvent"/> (handlers + <see cref="OnDeath"/>). An entity that
    /// is already dead (<see cref="ICombatEntity.IsDead"/>) is left alone and raises no second death event.
    /// Despawning/respawning is up to the game.</remarks>
    /// <param name="entity">Entity to kill.</param>
    /// <param name="killer">Credited killer, or null.</param>
    void Kill(ICombatEntity entity, ICombatEntity? killer = null);

    /// <summary>Fired per entity hit (single target or per-entity in AoE).</summary>
    event Action<HitEvent>? OnHit;

    /// <summary>Fired when any entity dies from combat.</summary>
    event Action<DeathEvent>? OnDeath;

    /// <summary>Fired after an AoE sweep completes with all hit results.</summary>
    event Action<SweepEvent>? OnSweep;
}
