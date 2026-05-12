/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Combat;

public readonly record struct HitEvent(
    ICombatEntity Attacker,
    ICombatEntity Target,
    int Damage,
    DamageTraits Traits,
    object? Context = null) : ICombatEventPayload
{
    public HitEvent(ICombatEntity attacker, ICombatEntity target, int damage, DamageFlags flags, object? context = null)
        : this(attacker, target, damage, new DamageTraits(flags), context) { }

    public DamageFlags Flags => Traits.Flags;
}

public record DeathEvent(ICombatEntity Entity, ICombatEntity? Killer, float X, float Y, float Z) : ICombatEventPayload;

public readonly record struct SweepEvent(ICombatEntity Attacker, SweepQuery3D Query, IReadOnlyList<HitResult> Hits) : ICombatEventPayload;
