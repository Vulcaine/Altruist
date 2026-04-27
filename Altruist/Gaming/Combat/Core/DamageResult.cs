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
    Killed = 1u << 31,
}

/// <summary>
/// Framework-level damage metadata.
/// </summary>
public readonly record struct DamageTraits(DamageFlags Flags)
{
    public static readonly DamageTraits None = new(DamageFlags.None);
}

/// <summary>
/// Result produced by a damage calculator before it is applied to a target.
/// </summary>
public readonly record struct DamageSpec(
    int Damage,
    DamageTraits Traits)
{
    public DamageSpec(int damage, DamageFlags flags)
        : this(damage, new DamageTraits(flags)) { }

    public DamageFlags Flags => Traits.Flags;
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
    public HitResult(ICombatEntity target, int damage, DamageFlags flags, bool killed)
        : this(target, damage, new DamageTraits(flags), killed) { }

    public DamageFlags Flags => Traits.Flags;
}

public readonly record struct SweepResult(
    ICombatEntity Attacker,
    SweepQuery Query,
    IReadOnlyList<HitResult> Hits);
