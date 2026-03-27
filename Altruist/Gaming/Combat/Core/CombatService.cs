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
    private readonly ILagCompensationService? _lagCompensation;
    private readonly ILogger _logger;

    public event Action<HitEvent>? OnHit;
    public event Action<DeathEvent>? OnDeath;
    public event Action<SweepEvent>? OnSweep;

    public CombatService(
        IDamageCalculator calculator,
        ILoggerFactory loggerFactory,
        IGameWorldOrganizer3D? worldOrganizer = null,
        ISpatialCollisionDispatcher? collisionDispatcher = null,
        ILagCompensationService? lagCompensation = null)
    {
        _calculator = calculator;
        _worldOrganizer = worldOrganizer;
        _collisionDispatcher = collisionDispatcher;
        _lagCompensation = lagCompensation;
        _logger = loggerFactory.CreateLogger<CombatService>();
    }

    public HitResult Attack(ICombatEntity attacker, ICombatEntity target)
    {
        // Transparent lag compensation: if enabled and client sent a tick, rewind
        if (_lagCompensation != null && !_lagCompensation.IsRewound)
        {
            var clientTick = PacketContext.ClientTick;
            if (clientTick > 0)
            {
                HitResult result = default!;
                _lagCompensation.RewindWorld(clientTick, () =>
                    result = AttackInternal(attacker, target));
                return result;
            }
        }
        return AttackInternal(attacker, target);
    }

    private HitResult AttackInternal(ICombatEntity attacker, ICombatEntity target)
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
        // Transparent lag compensation: if enabled and client sent a tick, rewind
        if (_lagCompensation != null && !_lagCompensation.IsRewound)
        {
            var clientTick = PacketContext.ClientTick;
            if (clientTick > 0)
            {
                SweepResult result = default!;
                _lagCompensation.RewindWorld(clientTick, () =>
                    result = SweepInternal(attacker, query, damage));
                return result;
            }
        }
        return SweepInternal(attacker, query, damage);
    }

    private SweepResult SweepInternal(ICombatEntity attacker, SweepQuery query, int? damage)
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
                hit = AttackInternal(attacker, target);

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

    private bool IsInSweep(ICombatEntity entity, SweepQuery query)
    {
        // Use compensated positions when rewound
        var (ex, ey, ez) = _lagCompensation != null
            ? _lagCompensation.Compensate(entity.VirtualId, entity.X, entity.Y, entity.Z)
            : (entity.X, entity.Y, entity.Z);

        return query.Type switch
        {
            SweepType.Sphere => IsInSphere(ex, ey, ez, query),
            SweepType.Cone => IsInCone(ex, ey, ez, query),
            SweepType.Line => IsInLine(ex, ey, ez, query),
            _ => false,
        };
    }

    private static bool IsInSphere(float ex, float ey, float ez, SweepQuery query)
    {
        var dx = ex - query.CenterX;
        var dy = ey - query.CenterY;
        var dz = ez - query.CenterZ;
        return dx * dx + dy * dy + dz * dz <= query.Range * query.Range;
    }

    private static bool IsInCone(float ex, float ey, float ez, SweepQuery query)
    {
        var dx = ex - query.CenterX;
        var dy = ey - query.CenterY;
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist > query.Range || dist < 0.001f) return false;

        var angleToTarget = MathF.Atan2(dy, dx);
        var angleDiff = NormalizeAngle(angleToTarget - query.Direction);
        var halfAngle = query.Angle * MathF.PI / 360f;
        return MathF.Abs(angleDiff) <= halfAngle;
    }

    private static bool IsInLine(float ex, float ey, float ez, SweepQuery query)
    {
        var dx = ex - query.CenterX;
        var dy = ey - query.CenterY;

        var dirX = MathF.Cos(query.Direction);
        var dirY = MathF.Sin(query.Direction);

        var dot = dx * dirX + dy * dirY;
        if (dot < 0 || dot > query.Range) return false;

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
