/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Numerics;
using Altruist.Physx.ThreeD;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.ThreeD;

/// <summary>Per-force handle: caller can read remaining time, cancel the
/// force, or attach lifecycle hooks. Created by
/// <see cref="IForceRuntime3D.ApplySustained(IPhysxBody3D, Vector3, float)"/>;
/// never instantiated directly.</summary>
public sealed class ForceAgent3D
{
    internal ForceAgent3D(IPhysxBody3D body, Vector3 velocity, float durationSeconds)
    {
        Body = body;
        Velocity = velocity;
        Duration = durationSeconds;
        Elapsed = 0f;
        IsActive = true;
    }

    public IPhysxBody3D Body { get; }

    /// <summary>World-space velocity (m/s) applied per tick. XZ components
    /// drive motion; Y is ignored so non-KCC bodies keep their gravity.</summary>
    public Vector3 Velocity { get; }

    public float Duration { get; }
    public float Elapsed { get; private set; }
    public bool IsActive { get; private set; }

    /// <summary>Normalized progress in [0, 1].</summary>
    public float T => Duration > 0f ? MathF.Min(1f, Elapsed / Duration) : 1f;

    /// <summary>Fires after every position write, with the per-tick delta
    /// actually applied (Y always 0). Use to mirror <c>body.Position</c>
    /// onto an entity-shaped wrapper's <c>Transform</c> / colliders / facing
    /// — anything the framework can't reach. The runtime catches and logs
    /// exceptions thrown from this hook so a buggy callback can't fault
    /// other active forces.</summary>
    public Action<IPhysxBody3D, Vector3>? OnApplied { get; set; }

    /// <summary>Invoked exactly once when the force expires (either Duration
    /// reached or <see cref="Cancel"/> called). Body is in its final
    /// force-driven position when this fires.</summary>
    public Action<IPhysxBody3D>? OnExpired { get; set; }

    /// <summary>Mark this force inactive. The runtime evicts cancelled
    /// agents at the next tick; <see cref="OnExpired"/> still fires.</summary>
    public void Cancel()
    {
        IsActive = false;
    }

    /// <summary>Called by the runtime each tick. Returns true while still
    /// active; false when finished (caller should evict).</summary>
    internal bool Tick(float dt)
    {
        if (!IsActive) return false;
        if (dt <= 0f) return true;

        Elapsed += dt;

        // XZ-only motion. Y is preserved so gravity / KCC vertical state
        // remains authoritative.
        var delta = new Vector3(Velocity.X * dt, 0f, Velocity.Z * dt);
        var pos = Body.Position;
        Body.Position = new Vector3(pos.X + delta.X, pos.Y, pos.Z + delta.Z);

        OnApplied?.Invoke(Body, delta);

        if (Elapsed >= Duration)
        {
            IsActive = false;
            return false;
        }
        return true;
    }
}

/// <summary>Stateful per-body force registry: register a sustained world
/// velocity for a duration, the runtime advances <see cref="IPhysxBody3D.Position"/>
/// by velocity × dt every tick until the force expires. Multiple concurrent
/// forces on the same body stack additively each tick — the runtime sums
/// active agents' velocities before writing position.
///
/// <para>Use for sustained physics-style influence on a body: knockback over
/// time, wind zones, tractor effects, water current, gravity wells, "pulled
/// toward boss" mechanics. Distinct from
/// <see cref="ITrajectoryRuntime3D"/>: trajectories are precomputed curves
/// the body follows blindly; forces are velocity terms that can stack and
/// be cancelled mid-flight.</para>
///
/// <para>Forces write <see cref="IPhysxBody3D.Position"/> directly. For
/// KCC-controlled bodies this bypasses sweep collision — use
/// <see cref="BodyNavigationExtensions3D.MoveToward(IPhysxBody3D, System.Numerics.Vector3, float, float, ITerrainProvider)"/>
/// instead when collision integrity matters. For kinematic bodies (mobs,
/// projectiles), position writes are the natural channel.</para></summary>
public interface IForceRuntime3D
{
    /// <summary>Apply <paramref name="worldVelocity"/> (m/s) to
    /// <paramref name="body"/> every tick for <paramref name="durationSeconds"/>.
    /// Y component of the velocity is ignored; the force is XZ-planar.
    /// Multiple concurrent forces stack additively.</summary>
    ForceAgent3D ApplySustained(IPhysxBody3D body, Vector3 worldVelocity, float durationSeconds);

    /// <summary>Sugar form: <paramref name="direction"/> normalized × <paramref name="speed"/>.
    /// If <paramref name="direction"/> is zero, no force is registered and
    /// the returned agent is already inactive.</summary>
    ForceAgent3D ApplySustained(IPhysxBody3D body, Vector3 direction, float speed, float durationSeconds);

    /// <summary>Cancel a specific agent (other forces on the body keep
    /// running). No-op if the agent is already inactive.</summary>
    void Cancel(ForceAgent3D agent);

    /// <summary>Cancel every active force on <paramref name="body"/>. Use
    /// on death, stun-clear, or any state transition that should reset
    /// physical influences.</summary>
    void CancelAll(IPhysxBody3D body);

    int ActiveCount { get; }

    /// <summary>Advance every active agent by <paramref name="dt"/> seconds.
    /// Tests drive this manually; in production the world coordinator calls it
    /// at a fixed 25 Hz (<see cref="ForceRuntime3D.FixedStep"/>).</summary>
    void Update(float dt);
}

/// <summary>
/// Advances the active force agents at a fixed 25 Hz (dt = 0.04 s), independent of the engine
/// frame rate: a fixed-mode <see cref="IWorldStepper"/> driven by the <see cref="WorldCoordinator"/>
/// (on the world step, after the frame's next-tick queue, cycles and effects in inline mode).
/// </summary>
[Service(typeof(IForceRuntime3D))]
[Service(typeof(IWorldStepper))]
[ConditionalOnConfig("altruist:game")]
public sealed class ForceRuntime3D : IForceRuntime3D, IWorldStepper
{
    private const int TickHz = 25;

    public StepMode Mode => StepMode.Fixed;
    public int FixedHz => TickHz;
    public void FixedStep(in FixedStep step) => Update(step.Dt);

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ForceAgent3D, byte> _agents = new();

    public ForceRuntime3D(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<ForceRuntime3D>();
    }

    public int ActiveCount => _agents.Count;

    public ForceAgent3D ApplySustained(IPhysxBody3D body, Vector3 worldVelocity, float durationSeconds)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (durationSeconds <= 0f) throw new ArgumentException("duration must be > 0", nameof(durationSeconds));

        var agent = new ForceAgent3D(body, worldVelocity, durationSeconds);
        _agents.TryAdd(agent, 0);
        return agent;
    }

    public ForceAgent3D ApplySustained(IPhysxBody3D body, Vector3 direction, float speed, float durationSeconds)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (durationSeconds <= 0f) throw new ArgumentException("duration must be > 0", nameof(durationSeconds));

        // Zero direction or non-positive speed → no-op agent (inactive).
        var len = MathF.Sqrt(direction.X * direction.X + direction.Z * direction.Z);
        if (len < 1e-6f || speed <= 0f)
        {
            var noop = new ForceAgent3D(body, Vector3.Zero, durationSeconds);
            noop.Cancel();
            return noop;
        }

        var unit = direction / len;
        return ApplySustained(body, unit * speed, durationSeconds);
    }

    public void Cancel(ForceAgent3D agent)
    {
        if (agent == null) return;
        agent.Cancel();
        if (_agents.TryRemove(agent, out _))
            FireExpired(agent);
    }

    public void CancelAll(IPhysxBody3D body)
    {
        if (body == null) return;
        foreach (var kv in _agents)
        {
            if (ReferenceEquals(kv.Key.Body, body))
            {
                kv.Key.Cancel();
                if (_agents.TryRemove(kv.Key, out _))
                    FireExpired(kv.Key);
            }
        }
    }

    /// <summary>Advances every agent by one 25 Hz step (the coordinator calls <see cref="FixedStep"/>).</summary>
    public void TickFrame() => Update(1f / TickHz);

    public void Update(float dt)
    {
        if (dt <= 0f || _agents.IsEmpty) return;

        foreach (var kv in _agents)
        {
            var agent = kv.Key;
            try
            {
                if (!agent.Tick(dt))
                {
                    if (_agents.TryRemove(agent, out _))
                        FireExpired(agent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[ForceRuntime3D] agent tick threw — removing it.");
                if (_agents.TryRemove(agent, out _))
                    FireExpired(agent);
            }
        }
    }

    private void FireExpired(ForceAgent3D agent)
    {
        try { agent.OnExpired?.Invoke(agent.Body); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ForceRuntime3D] OnExpired callback threw.");
        }
    }
}
