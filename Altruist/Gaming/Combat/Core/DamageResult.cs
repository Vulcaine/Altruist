/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Combat;

/// <summary>
/// Bit flags describing how a hit landed. Set by your <see cref="IDamageCalculator"/> (or the caller of
/// <see cref="ICombatService.ApplyDamage"/>); the framework itself only sets <see cref="Miss"/> (target already dead)
/// and <see cref="Killed"/> (on the <see cref="HitEvent"/> of a lethal hit).
/// </summary>
/// <remarks>The framework does not interpret <see cref="Dodge"/>/<see cref="Block"/>/<see cref="Critical"/>: damage
/// is applied as given, so a calculator reporting a dodge should also return 0 damage.</remarks>
[Flags]
public enum DamageFlags : uint
{
    /// <summary>No flags.</summary>
    None = 0,
    /// <summary>Ordinary hit (default for most calls).</summary>
    Normal = 1,
    /// <summary>Critical hit.</summary>
    Critical = 2,
    /// <summary>Hit ignored defenses/armor.</summary>
    Penetrate = 4,
    /// <summary>Target dodged.</summary>
    Dodge = 8,
    /// <summary>Target blocked.</summary>
    Block = 16,
    /// <summary>Attack missed; also returned by the service when the target was already dead.</summary>
    Miss = 32,
    /// <summary>The hit reduced the target to 0 health. Set on <see cref="HitEvent.Flags"/> only (not on the returned
    /// <see cref="HitResult"/>; use <see cref="HitResult.Killed"/> there).</summary>
    Killed = 1u << 31,
}

/// <summary>
/// Framework-level damage metadata.
/// </summary>
/// <param name="Flags">The hit's damage flags.</param>
public readonly record struct DamageTraits(DamageFlags Flags)
{
    /// <summary>Traits with no flags.</summary>
    public static readonly DamageTraits None = new(DamageFlags.None);
}

/// <summary>
/// Result produced by a damage calculator before it is applied to a target.
/// </summary>
/// <param name="Damage">Damage to subtract from the target's health (no minimum is enforced here).</param>
/// <param name="Traits">Flags describing the hit.</param>
public readonly record struct DamageSpec(
    int Damage,
    DamageTraits Traits)
{
    /// <summary>Creates a spec from raw <see cref="DamageFlags"/>.</summary>
    /// <param name="damage">Damage to apply.</param>
    /// <param name="flags">Flags describing the hit.</param>
    public DamageSpec(int damage, DamageFlags flags)
        : this(damage, new DamageTraits(flags)) { }

    /// <summary>Shortcut for <c>Traits.Flags</c>.</summary>
    public DamageFlags Flags => Traits.Flags;
}

/// <summary>Shape of an area sweep (<see cref="SweepQuery3D"/>, <see cref="Altruist.Gaming.Combat.TwoD.SweepQuery2D"/>).</summary>
public enum SweepType
{
    /// <summary>Circle (planar spaces) or sphere (<see cref="SweepSpace.ThreeD"/>) of radius <c>Range</c> around the center.</summary>
    Sphere,
    /// <summary>Wedge/cone of length <c>Range</c> and full opening <c>Angle</c> (degrees) around the direction.</summary>
    Cone,
    /// <summary>Capsule-like strip from the center along the direction, <c>Range</c> long and <c>Width</c> half-width.</summary>
    Line,
}

/// <summary>Outcome of one hit returned by <see cref="ICombatService.Attack"/>, <see cref="ICombatService.ApplyDamage"/>
/// and per target in <see cref="SweepResult.Hits"/>.</summary>
/// <param name="Target">Entity that was hit.</param>
/// <param name="Damage">Damage applied (0 when the target was already dead).</param>
/// <param name="Traits">Flags as passed in / computed; does not include <see cref="DamageFlags.Killed"/>.</param>
/// <param name="Killed">True when this hit reduced the target to 0 health.</param>
public readonly record struct HitResult(
    ICombatEntity Target,
    int Damage,
    DamageTraits Traits,
    bool Killed)
{
    /// <summary>Creates a result from raw <see cref="DamageFlags"/>.</summary>
    /// <param name="target">Entity that was hit.</param>
    /// <param name="damage">Damage applied.</param>
    /// <param name="flags">Hit flags.</param>
    /// <param name="killed">True when the hit was lethal.</param>
    public HitResult(ICombatEntity target, int damage, DamageFlags flags, bool killed)
        : this(target, damage, new DamageTraits(flags), killed) { }

    /// <summary>Shortcut for <c>Traits.Flags</c>.</summary>
    public DamageFlags Flags => Traits.Flags;
}

/// <summary>Outcome of <see cref="ICombatService.Sweep"/>.</summary>
/// <param name="Attacker">Entity that performed the sweep.</param>
/// <param name="Query">The tested shape.</param>
/// <param name="Hits">One entry per entity hit, in hit order; capped by <see cref="SweepQuery3D.MaxTargets"/>.</param>
public readonly record struct SweepResult(
    ICombatEntity Attacker,
    SweepQuery3D Query,
    IReadOnlyList<HitResult> Hits);
