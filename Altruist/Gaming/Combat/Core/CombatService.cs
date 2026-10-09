/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using Altruist;
using Altruist.Gaming.ThreeD;
using Altruist.Numerics;
using Altruist.ThreeD.Numerics;
using Microsoft.Extensions.Logging;
using System.Numerics;

namespace Altruist.Gaming.Combat;

/// <summary>
/// Placeholder damage calculator — used as fallback when no game-specific
/// IDamageCalculator is registered. Games should register their own
/// IDamageCalculator implementation via [Service(typeof(IDamageCalculator))].
/// </summary>
/// <remarks>Formula: <c>max(1, attacker.GetAttackPower() - target.GetDefensePower())</c> with
/// <see cref="DamageFlags.Normal"/>. Both powers default to 0 on <see cref="ICombatEntity"/>, so without overrides
/// every hit deals 1. Subclass and override <see cref="Calculate"/> to tweak it while keeping the fallback shape.</remarks>
public class DefaultDamageCalculator : IDamageCalculator
{
    /// <inheritdoc/>
    public virtual DamageSpec Calculate(ICombatEntity attacker, ICombatEntity target)
        => new(Math.Max(1, attacker.GetAttackPower() - target.GetDefensePower()), DamageFlags.Normal);
}

/// <summary>
/// Default <see cref="ICombatService"/> implementation (singleton via <c>[Service(typeof(ICombatService))]</c>).
/// Resolve <see cref="ICombatService"/> from DI; subclass only to change sweep/damage internals.
/// </summary>
/// <remarks>
/// Sweep candidates come from 3D world 0's cached object snapshot. Sphere queries in <see cref="SweepSpace.PlanarXZ"/>
/// or <see cref="SweepSpace.ThreeD"/> over more than 50 objects use a spatial hash broadphase (500-unit cells, rebuilt
/// when the snapshot version changes); everything else is a linear scan. Not thread-safe (shared broadphase buffers).
/// </remarks>
[Service(typeof(ICombatService))]
public class CombatService : ICombatService
{
    private readonly IDamageCalculator _calculator;
    private readonly IGameWorldOrganizer3D? _worldOrganizer;
    private readonly ICombatEventDispatcher? _combatEvents;
    private readonly ILagCompensationService? _lagCompensation;
    private readonly ILogger _logger;

    /// <inheritdoc/>
    public event Action<HitEvent>? OnHit;
    /// <inheritdoc/>
    public event Action<DeathEvent>? OnDeath;
    /// <inheritdoc/>
    public event Action<SweepEvent>? OnSweep;

    /// <summary>Created by DI; all dependencies except the logger factory are optional.</summary>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <param name="calculator">Game damage formula; <see cref="DefaultDamageCalculator"/> when null.</param>
    /// <param name="worldOrganizer">3D world source for sweeps; sweeps find nothing when null.</param>
    /// <param name="lagCompensation">Enables transparent rewind in <see cref="Attack"/>/<see cref="Sweep"/> when present
    /// (registered when <c>altruist:game:lag-compensation</c> is configured).</param>
    /// <param name="combatEvents">Dispatcher for <c>[CombatEvent]</c> handlers; handlers are skipped when null.</param>
    public CombatService(
        ILoggerFactory loggerFactory,
        IDamageCalculator? calculator = null,
        IGameWorldOrganizer3D? worldOrganizer = null,
        ILagCompensationService? lagCompensation = null,
        ICombatEventDispatcher? combatEvents = null)
    {
        _calculator = calculator ?? new DefaultDamageCalculator();
        _worldOrganizer = worldOrganizer;
        _combatEvents = combatEvents;
        _lagCompensation = lagCompensation;
        _logger = loggerFactory.CreateLogger<CombatService>();
        _logger.LogInformation("CombatService using damage calculator: {Type}", _calculator.GetType().FullName);
    }

    /// <inheritdoc/>
    public HitResult Attack(ICombatEntity attacker, ICombatEntity target, object? context = null)
    {
        // Transparent lag compensation: if enabled and client sent a tick, rewind
        if (_lagCompensation != null && !_lagCompensation.IsRewound)
        {
            var clientTick = PacketContext.ClientTick;
            if (clientTick > 0)
                return _lagCompensation.RewindWorld(clientTick, () => AttackInternal(attacker, target, context));
        }
        return AttackInternal(attacker, target, context);
    }

    private HitResult AttackInternal(ICombatEntity attacker, ICombatEntity target, object? context = null)
    {
        if (target.IsDead)
            return new HitResult(target, 0, DamageFlags.Miss, false);

        var spec = _calculator.Calculate(attacker, target);
        return ApplyDamage(attacker, target, spec.Damage, spec.Flags, context);
    }

    /// <inheritdoc/>
    public HitResult ApplyDamage(ICombatEntity source, ICombatEntity target, int damage, DamageFlags flags = DamageFlags.Normal, object? context = null)
    {
        if (target.IsDead)
            return new HitResult(target, 0, DamageFlags.Miss, false);

        target.Health = Math.Max(0, target.Health - damage);
        bool killed = target.Health <= 0;

        var hitEvent = new HitEvent(source, target, damage, killed ? flags.SetFlag(DamageFlags.Killed) : flags, context);
        _combatEvents?.Dispatch(hitEvent, source, target);
        OnHit?.Invoke(hitEvent);

        if (killed)
            Kill(target, source);

        return new HitResult(target, damage, flags, killed);
    }

    /// <inheritdoc/>
    public SweepResult Sweep(ICombatEntity attacker, SweepQuery3D query, int? damage = null, DamageFlags flags = DamageFlags.Normal, object? context = null)
    {
        // Transparent lag compensation: if enabled and client sent a tick, rewind
        if (_lagCompensation != null && !_lagCompensation.IsRewound)
        {
            var clientTick = PacketContext.ClientTick;
            if (clientTick > 0)
                return _lagCompensation.RewindWorld(clientTick, () => SweepInternal(attacker, query, damage, flags, context));
        }
        return SweepInternal(attacker, query, damage, flags, context);
    }

    private SweepResult SweepInternal(ICombatEntity attacker, SweepQuery3D query, int? damage, DamageFlags flags, object? context)
    {
        var targets = FindEntitiesInSweep(query);
        var hits = new List<HitResult>();

        foreach (var target in targets)
        {
            if (target.VirtualId == attacker.VirtualId) continue;
            if (target.IsDead) continue;

            HitResult hit;
            if (damage.HasValue)
                hit = ApplyDamage(attacker, target, damage.Value, flags, context);
            else
                hit = AttackInternal(attacker, target, context);

            hits.Add(hit);

            if (query.MaxTargets > 0 && hits.Count >= query.MaxTargets)
                break;
        }

        var result = new SweepResult(attacker, query, hits);
        var sweepEvent = new SweepEvent(attacker, query, hits);
        _combatEvents?.Dispatch(sweepEvent, attacker);
        OnSweep?.Invoke(sweepEvent);
        return result;
    }

    /// <inheritdoc/>
    public void Kill(ICombatEntity entity, ICombatEntity? killer = null)
    {
        entity.Health = 0;
        var deathEvent = new DeathEvent(entity, killer, entity.X, entity.Y, entity.Z);
        if (killer != null)
            _combatEvents?.Dispatch(deathEvent, entity, killer);
        else
            _combatEvents?.Dispatch(deathEvent, entity);
        OnDeath?.Invoke(deathEvent);
    }

    // Spatial broadphase for AoE sweep queries — avoids iterating all entities
    private readonly SpatialHashGrid _sweepGrid = new(cellSize: 500f);
    private readonly List<int> _sweepGridBuffer = new(128);
    private int _cachedSnapshotVersion = -1;

    private List<ICombatEntity> FindEntitiesInSweep(SweepQuery3D query)
    {
        var results = new List<ICombatEntity>();

        var world = _worldOrganizer?.GetWorld(0);
        if (world == null) return results;

        var (allObjects, _) = world.GetCachedSnapshot();

        // Use spatial grid for sphere queries (most common AoE type)
        if (query.Type == SweepType.Sphere && allObjects.Count > 50 && CanUseSpatialGrid(query))
        {
            // The snapshot's List reference is stable across rebuilds, so we
            // compare the version counter instead. Without this, the grid keeps
            // indices into a stale (larger) snapshot and crashes with an
            // IndexOutOfRange the next time a smaller rebuild lands underneath.
            if (_cachedSnapshotVersion != world.SnapshotVersion)
            {
                _sweepGrid.Build(allObjects);
                _cachedSnapshotVersion = world.SnapshotVersion;
            }

            _sweepGrid.QueryRadius(query.CenterX, query.CenterY, query.CenterZ, query.Range, _sweepGridBuffer);
            for (int i = 0; i < _sweepGridBuffer.Count; i++)
            {
                var obj = allObjects[_sweepGridBuffer[i]];
                if (obj is not ICombatEntity entity || entity.IsDead) continue;
                if (query.Filter != null && !query.Filter(entity)) continue;
                if (IsInSweep(entity, query))
                    results.Add(entity);
            }
        }
        else
        {
            // Fallback: iterate all for cone/line queries or small worlds
            foreach (var obj in allObjects)
            {
                if (obj is not ICombatEntity entity || entity.IsDead) continue;
                if (query.Filter != null && !query.Filter(entity)) continue;
                if (IsInSweep(entity, query))
                    results.Add(entity);
            }
        }

        return results;
    }

    private bool IsInSweep(ICombatEntity entity, SweepQuery3D query)
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

    private static bool IsInSphere(float ex, float ey, float ez, SweepQuery3D query)
    {
        var dx = ex - query.CenterX;
        return query.Space switch
        {
            SweepSpace.PlanarXZ => dx * dx + (ez - query.CenterZ) * (ez - query.CenterZ) <= query.Range * query.Range,
            SweepSpace.PlanarYZ => (ey - query.CenterY) * (ey - query.CenterY) + (ez - query.CenterZ) * (ez - query.CenterZ) <= query.Range * query.Range,
            SweepSpace.ThreeD => dx * dx + (ey - query.CenterY) * (ey - query.CenterY) + (ez - query.CenterZ) * (ez - query.CenterZ) <= query.Range * query.Range,
            _ => dx * dx + (ey - query.CenterY) * (ey - query.CenterY) <= query.Range * query.Range,
        };
    }

    private static bool IsInCone(float ex, float ey, float ez, SweepQuery3D query)
    {
        if (query.Space == SweepSpace.ThreeD)
            return IsInCone3D(ex, ey, ez, query);

        var (dx, dy) = GetPlanarDelta(ex, ey, ez, query);
        var dist = MathF.Sqrt(dx * dx + dy * dy);
        if (dist > query.Range || dist < 0.001f) return false;

        var angleToTarget = MathF.Atan2(dy, dx);
        var angleDiff = Angle.Normalize(angleToTarget - query.Direction);
        var halfAngle = query.Angle * MathF.PI / 360f;
        return MathF.Abs(angleDiff) <= halfAngle;
    }

    private static bool IsInLine(float ex, float ey, float ez, SweepQuery3D query)
    {
        if (query.Space == SweepSpace.ThreeD)
            return IsInLine3D(ex, ey, ez, query);

        var (dx, dy) = GetPlanarDelta(ex, ey, ez, query);

        var dirX = MathF.Cos(query.Direction);
        var dirY = MathF.Sin(query.Direction);

        var dot = dx * dirX + dy * dirY;
        if (dot < 0 || dot > query.Range) return false;

        var perpDist = MathF.Abs(-dx * dirY + dy * dirX);
        return perpDist <= (query.Width > 0f ? query.Width : 200f);
    }

    private static bool IsInCone3D(float ex, float ey, float ez, SweepQuery3D query)
    {
        var delta = new Vector3(ex - query.CenterX, ey - query.CenterY, ez - query.CenterZ);
        float distSq = delta.LengthSquared();
        if (distSq > query.Range * query.Range)
            return false;
        if (distSq < 0.001f)
            return true;

        var direction = GetDirectionVector3D(query);
        if (direction.LengthSquared() < 0.0001f)
            return false;

        direction = Vector3.Normalize(direction);
        float dot = Vector3.Dot(Vector3.Normalize(delta), direction);
        float cosHalf = MathF.Cos(query.Angle * MathF.PI / 360f);
        return dot >= cosHalf;
    }

    private static bool IsInLine3D(float ex, float ey, float ez, SweepQuery3D query)
    {
        var direction = GetDirectionVector3D(query);
        if (direction.LengthSquared() < 0.0001f)
            return false;

        direction = Vector3.Normalize(direction);
        var delta = new Vector3(ex - query.CenterX, ey - query.CenterY, ez - query.CenterZ);
        float along = Vector3.Dot(delta, direction);
        if (along < 0 || along > query.Range)
            return false;

        float perpSq = delta.LengthSquared() - along * along;
        float width = query.Width > 0f ? query.Width : 200f;
        return perpSq <= width * width;
    }

    private static (float dx, float dy) GetPlanarDelta(float ex, float ey, float ez, SweepQuery3D query)
        => query.Space switch
        {
            SweepSpace.PlanarXZ => (ex - query.CenterX, ez - query.CenterZ),
            SweepSpace.PlanarYZ => (ey - query.CenterY, ez - query.CenterZ),
            _ => (ex - query.CenterX, ey - query.CenterY),
        };

    private static Vector3 GetDirectionVector3D(SweepQuery3D query)
        => query.Space switch
        {
            SweepSpace.PlanarXZ => new Vector3(MathF.Sin(query.Direction), 0f, MathF.Cos(query.Direction)),
            SweepSpace.PlanarYZ => new Vector3(0f, MathF.Cos(query.Direction), MathF.Sin(query.Direction)),
            SweepSpace.PlanarXY => new Vector3(MathF.Cos(query.Direction), MathF.Sin(query.Direction), 0f),
            _ => new Vector3(query.DirectionX, query.DirectionY, query.DirectionZ),
        };

    private static bool CanUseSpatialGrid(SweepQuery3D query)
        => query.Space is SweepSpace.PlanarXZ or SweepSpace.ThreeD;

}
