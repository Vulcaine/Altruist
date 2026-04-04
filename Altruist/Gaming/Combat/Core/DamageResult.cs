/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Combat;

[Flags]
public enum DamageFlags : byte
{
    None = 0,
    Normal = 1,
    Critical = 2,
    Penetrate = 4,
    Dodge = 8,
    Block = 16,
    Miss = 32,
    Poison = 64,
    Magic = 128,
}

public enum SweepType
{
    Sphere,
    Cone,
    Line,
}

public record HitResult(
    ICombatEntity Target,
    int Damage,
    DamageFlags Flags,
    bool Killed);

public record SweepResult(
    ICombatEntity Attacker,
    SweepQuery Query,
    IReadOnlyList<HitResult> Hits);
