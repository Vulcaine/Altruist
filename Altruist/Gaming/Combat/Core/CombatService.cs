/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist.Gaming.ThreeD;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.Combat;

[Service(typeof(IDamageCalculator))]
public class DefaultDamageCalculator : IDamageCalculator
{
    public virtual int Calculate(ICombatEntity attacker, ICombatEntity target)
        => Math.Max(1, attacker.GetAttackPower() - target.GetDefensePower());
}

[Service(typeof(ICombatService))]
public class CombatService : ICombatService
{
    private readonly IDamageCalculator _calculator;
    private readonly IGameWorldOrganizer3D? _worldOrganizer;
    private readonly ISpatialCollisionDispatcher? _collisionDispatcher;
    private readonly ILogger _logger;

    public event Action<HitEvent>? OnHit;
    public event Action<DeathEvent>? OnDeath;
    public event Action<SweepEvent>? OnSweep;

    public CombatService(
        IDamageCalculator calculator,
        ILoggerFactory loggerFactory,
        IGameWorldOrganizer3D? worldOrganizer = null,
        ISpatialCollisionDispatcher? collisionDispatcher = null)
    {
        _calculator = calculator;
        _worldOrganizer = worldOrganizer;
        _collisionDispatcher = collisionDispatcher;
        _logger = loggerFactory.CreateLogger<CombatService>();
    }

    public HitResult Attack(ICombatEntity attacker, ICombatEntity target)
    {
        if (target.IsDead)
            return new HitResult(target, 0, DamageFlags.Miss, false);

        var damage = _calculator.Calculate(attacker, target);
        return ApplyDamage(attacker, target, damage, DamageFlags.Normal);
    }

    public HitResult ApplyDamage(ICombatEntity source, ICombatEntity target, int damage, DamageFlags flags = DamageFlags.Normal)
    {
        if (target.IsDead)
            return new HitResult(target, 0, DamageFlags.Miss, false);

        target.Health = Math.Max(0, target.Health - damage);
        bool killed = target.Health <= 0;

        // Fire collision handlers (same API as physics collision events)
        _collisionDispatcher?.DispatchHit(source, target);

        OnHit?.Invoke(new HitEvent(source, target, damage, flags));

        if (killed)
            Kill(target, source);

        return new HitResult(target, damage, flags, killed);
    }

    public SweepResult Sweep(ICombatEntity attacker, SweepQuery query, int? damage = null)
    {
        var targets = FindEntitiesInSweep(query);
        var hits = new List<HitResult>();

        foreach (var target in targets)
        {
            if (target.VirtualId == attacker.VirtualId) continue;
            if (target.IsDead) continue;

            HitResult hit;
            if (damage.HasValue)
                hit = ApplyDamage(attacker, target, damage.Value, DamageFlags.Normal);
            else
                hit = Attack(attacker, target);

            hits.Add(hit);

            if (query.MaxTargets > 0 && hits.Count >= query.MaxTargets)
                break;
        }

        var result = new SweepResult(attacker, query, hits);
        OnSweep?.Invoke(new SweepEvent(attacker, query, hits));
        return result;
    }

    public void Kill(ICombatEntity entity, ICombatEntity? killer = null)
    {
        entity.Health = 0;
        OnDeath?.Invoke(new DeathEvent(entity, killer, entity.X, entity.Y, entity.Z));
    }

    private List<ICombatEntity> FindEntitiesInSweep(SweepQuery query)
    {
        var results = new List<ICombatEntity>();

        // Use world spatial queries if available
        var world = _worldOrganizer?.GetWorld(0);
        if (world != null)
        {
            var allObjects = world.FindAllObjects<IWorldObject3D>();
            foreach (var obj in allObjects)
            {
                if (obj is not ICombatEntity entity || entity.IsDead) continue;

                if (IsInSweep(entity, query))
                    results.Add(entity);
            }
            return results;
        }

        return results;
    }

    private static bool IsInSweep(ICombatEntity entity, SweepQuery query)
    {
        return query.Type switch
        {
            SweepType.Sphere => IsInSphere(entity, query),
            SweepType.Cone => IsInCone(entity, query),
            SweepType.Line => IsInLine(entity, query),
            _ => false,
        };
    }

    private static bool IsInSphere(ICombatEntity entity, SweepQuery query)
    {
        var dx = entity.X - query.CenterX;
        var dy = entity.Y - query.CenterY;
        var dz = entity.Z - query.CenterZ;
        return dx * dx + dy * dy + dz * dz <= query.Range * query.Range;
    }

    private static bool IsInCone(ICombatEntity entity, SweepQuery query)
    {
        var dx = entity.X - query.CenterX;
        var dy = entity.Y - query.CenterY;
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist > query.Range || dist < 0.001f) return false;

        var angleToTarget = MathF.Atan2(dy, dx);
        var angleDiff = NormalizeAngle(angleToTarget - query.Direction);
        var halfAngle = query.Angle * MathF.PI / 360f; // half angle in radians
        return MathF.Abs(angleDiff) <= halfAngle;
    }

    private static bool IsInLine(ICombatEntity entity, SweepQuery query)
    {
        var dx = entity.X - query.CenterX;
        var dy = entity.Y - query.CenterY;

        var dirX = MathF.Cos(query.Direction);
        var dirY = MathF.Sin(query.Direction);

        // Project onto line direction
        var dot = dx * dirX + dy * dirY;
        if (dot < 0 || dot > query.Range) return false;

        // Perpendicular distance (line width = 200 units default)
        var perpDist = MathF.Abs(-dx * dirY + dy * dirX);
        return perpDist <= 200f;
    }

    private static float NormalizeAngle(float angle)
    {
        while (angle > MathF.PI) angle -= 2 * MathF.PI;
        while (angle < -MathF.PI) angle += 2 * MathF.PI;
        return angle;
    }
}
