/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Combat;

/// <summary>
/// Raised by <see cref="ICombatService"/> once per entity that takes damage (single attacks, each target of a sweep,
/// and direct <see cref="ICombatService.ApplyDamage"/> calls). Delivered through <see cref="ICombatService.OnHit"/>
/// and to <see cref="CombatEventAttribute"/> handlers with actors (attacker, target).
/// </summary>
/// <remarks>Raised after <see cref="ICombatEntity.Health"/> has been reduced; if the hit was lethal,
/// <see cref="Flags"/> includes <see cref="DamageFlags.Killed"/> and a <see cref="DeathEvent"/> follows.</remarks>
/// <param name="Attacker">Source of the damage.</param>
/// <param name="Target">Entity that took the damage.</param>
/// <param name="Damage">Damage applied (before clamping health at zero).</param>
/// <param name="Traits">Damage metadata (flags).</param>
/// <param name="Context">Opaque caller data passed through from the combat call (e.g. skill id), or null.</param>
public readonly record struct HitEvent(
    ICombatEntity Attacker,
    ICombatEntity Target,
    int Damage,
    DamageTraits Traits,
    object? Context = null) : ICombatEventPayload
{
    /// <summary>Creates a hit event from raw <see cref="DamageFlags"/>.</summary>
    /// <param name="attacker">Source of the damage.</param>
    /// <param name="target">Entity that took the damage.</param>
    /// <param name="damage">Damage applied.</param>
    /// <param name="flags">Damage flags.</param>
    /// <param name="context">Opaque caller data, or null.</param>
    public HitEvent(ICombatEntity attacker, ICombatEntity target, int damage, DamageFlags flags, object? context = null)
        : this(attacker, target, damage, new DamageTraits(flags), context) { }

    /// <summary>Shortcut for <c>Traits.Flags</c>.</summary>
    public DamageFlags Flags => Traits.Flags;
}

/// <summary>
/// Raised by <see cref="ICombatService.Kill"/> (directly, or after a lethal hit). Delivered through
/// <see cref="ICombatService.OnDeath"/> and to <see cref="CombatEventAttribute"/> handlers with actors
/// (victim, killer), or (victim) alone when there is no killer.
/// </summary>
/// <param name="Entity">The entity that died (its health is already 0).</param>
/// <param name="Killer">Who dealt the killing blow, or null (environment, script).</param>
/// <param name="X">Victim X position at death (current, not lag-compensated).</param>
/// <param name="Y">Victim Y position at death.</param>
/// <param name="Z">Victim Z position at death.</param>
public record DeathEvent(ICombatEntity Entity, ICombatEntity? Killer, float X, float Y, float Z) : ICombatEventPayload;

/// <summary>
/// Raised once after an area sweep (<see cref="ICombatService.Sweep"/>) finishes, after all per-target
/// <see cref="HitEvent"/>s. Delivered through <see cref="ICombatService.OnSweep"/> and to
/// <see cref="CombatEventAttribute"/> handlers with the attacker as the only actor.
/// </summary>
/// <param name="Attacker">Entity that performed the sweep.</param>
/// <param name="Query">The shape that was tested.</param>
/// <param name="Hits">One result per entity hit, in hit order (may be empty).</param>
public readonly record struct SweepEvent(ICombatEntity Attacker, SweepQuery3D Query, IReadOnlyList<HitResult> Hits) : ICombatEventPayload;
