/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

namespace Altruist.Gaming.Combat;

public record HitEvent(ICombatEntity Attacker, ICombatEntity Target, int Damage, DamageFlags Flags);
public record DeathEvent(ICombatEntity Entity, ICombatEntity? Killer, float X, float Y, float Z);
public record SweepEvent(ICombatEntity Attacker, SweepQuery Query, IReadOnlyList<HitResult> Hits);
