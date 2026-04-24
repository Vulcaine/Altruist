/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Combat;

[Flags]
public enum DamageFlags : uint
{
    None = 0,
    Normal = 1,
    Critical = 2,
    Penetrate = 4,
    Dodge = 8,
    Block = 16,
    Miss = 32,
}

/// <summary>
/// Framework-level damage metadata. Core flags stay small and reusable, while
/// CustomFlags are reserved for game-specific semantics.
/// </summary>
public readonly record struct DamageTraits(
    DamageFlags Flags,
    uint CustomFlags = 0)
{
    public static readonly DamageTraits None = new(DamageFlags.None, 0);
}

/// <summary>
/// Result produced by a damage calculator before it is applied to a target.
/// </summary>
public readonly record struct DamageSpec(
    int Damage,
    DamageTraits Traits)
{
    public DamageSpec(int damage, DamageFlags flags, uint customFlags = 0)
        : this(damage, new DamageTraits(flags, customFlags)) { }

    public DamageFlags Flags => Traits.Flags;
    public uint CustomFlags => Traits.CustomFlags;
}

public enum SweepType
{
    Sphere,
    Cone,
    Line,
}

public readonly record struct HitResult(
    ICombatEntity Target,
    int Damage,
    DamageTraits Traits,
    bool Killed)
{
    public HitResult(ICombatEntity target, int damage, DamageFlags flags, bool killed, uint customFlags = 0)
        : this(target, damage, new DamageTraits(flags, customFlags), killed) { }

    public DamageFlags Flags => Traits.Flags;
    public uint CustomFlags => Traits.CustomFlags;
}

public readonly record struct SweepResult(
    ICombatEntity Attacker,
    SweepQuery Query,
    IReadOnlyList<HitResult> Hits);
