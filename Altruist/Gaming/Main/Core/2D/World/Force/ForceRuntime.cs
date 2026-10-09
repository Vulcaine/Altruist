/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Numerics;
using Altruist.Physx.TwoD;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.TwoD;

/// <summary>2D mirror of <see cref="Altruist.Gaming.ThreeD.ForceAgent3D"/>. Force is full-planar
/// (X and Y both drive motion) — there is no preserved axis since 2D has
/// no "gravity axis" equivalent of 3D's Y.
/// <para>One active sustained push created by <see cref="IForceRuntime2D.ApplySustained(IPhysxBody2D,Vector2,float)"/>:
/// each runtime step it moves <see cref="Body"/> by <c>Velocity * dt</c> by writing
/// <c>Body.Position</c> directly (a kinematic displacement: it does not change the body's velocity and is
/// not swept against colliders) until <see cref="Duration"/> has elapsed or it is cancelled.</para></summary>
public sealed class ForceAgent2D
{
    internal ForceAgent2D(IPhysxBody2D body, Vector2 velocity, float durationSeconds)
    {
        Body = body;
        Velocity = velocity;
        Duration = durationSeconds;
        Elapsed = 0f;
        IsActive = true;
    }

    /// <summary>The body being pushed.</summary>
    public IPhysxBody2D Body { get; }
    /// <summary>Displacement rate in world units per second (zero for a no-op agent).</summary>
    public Vector2 Velocity { get; }
    /// <summary>Total push time in seconds.</summary>
    public float Duration { get; }
    /// <summary>Seconds applied so far.</summary>
    public float Elapsed { get; private set; }
    /// <summary>False once expired or cancelled.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Progress 0..1: <c>Elapsed / Duration</c>, clamped (1 when the duration is not positive).</summary>
    public float T => Duration > 0f ? MathF.Min(1f, Elapsed / Duration) : 1f;

    /// <summary>Called after each step with the body and the displacement just applied (on the world step thread).</summary>
    public Action<IPhysxBody2D, Vector2>? OnApplied { get; set; }
    /// <summary>Called once when the agent ends (expired, cancelled through the runtime, or its tick threw).</summary>
    public Action<IPhysxBody2D>? OnExpired { get; set; }

    /// <summary>Stops the push. Prefer <see cref="IForceRuntime2D.Cancel"/>, which also removes the agent
    /// and fires <see cref="OnExpired"/> immediately; after this call the runtime does so on its next step.</summary>
    public void Cancel()
    {
        IsActive = false;
    }

    internal bool Tick(float dt)
    {
        if (!IsActive) return false;
        if (dt <= 0f) return true;

        Elapsed += dt;

        var delta = Velocity * dt;
        Body.Position = Body.Position + delta;

        OnApplied?.Invoke(Body, delta);

        if (Elapsed >= Duration)
        {
            IsActive = false;
            return false;
        }
        return true;
    }
}

/// <summary>2D mirror of <see cref="Altruist.Gaming.ThreeD.IForceRuntime3D"/>.
/// Same per-body per-tick driver pattern. 2D bodies move on the full XY
/// plane (no preserved axis).
/// <para>Use it for timed, fire-and-forget displacements (knockback, dash, conveyor push) that should keep
/// going over several ticks without game code tracking them. It moves the body's position directly; for
/// a one-off velocity change of a physics-simulated body use <see cref="GameplayVerbs2D"/> (e.g.
/// <c>PushAlong</c>, <c>LaunchAlong</c>) instead.</para>
/// <example><code>
/// var push = forces.ApplySustained(body, direction: hitNormal, speed: 6f, durationSeconds: 0.25f);
/// push.OnExpired = b =&gt; b.LinearVelocity = Vector2.Zero;
/// </code></example></summary>
public interface IForceRuntime2D
{
    /// <summary>Starts moving <paramref name="body"/> at <paramref name="worldVelocity"/> (units/s) for
    /// <paramref name="durationSeconds"/>. Several agents on one body add up.</summary>
    /// <returns>The agent, for progress, callbacks or cancelling.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="durationSeconds"/> is not positive.</exception>
    ForceAgent2D ApplySustained(IPhysxBody2D body, Vector2 worldVelocity, float durationSeconds);
    /// <summary>Like <see cref="ApplySustained(IPhysxBody2D,Vector2,float)"/> with
    /// <c>normalize(direction) * speed</c>. A near-zero direction or non-positive speed returns an
    /// already-cancelled agent that is never scheduled.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="durationSeconds"/> is not positive.</exception>
    ForceAgent2D ApplySustained(IPhysxBody2D body, Vector2 direction, float speed, float durationSeconds);
    /// <summary>Stops and removes the agent and fires its <c>OnExpired</c> (null is ignored).</summary>
    void Cancel(ForceAgent2D agent);
    /// <summary>Cancels every agent pushing <paramref name="body"/> (e.g. when it dies or respawns).</summary>
    void CancelAll(IPhysxBody2D body);
    /// <summary>Number of scheduled agents.</summary>
    int ActiveCount { get; }
    /// <summary>Advances every agent by <paramref name="dt"/> seconds. Called by the engine at the
    /// runtime's fixed rate; call it yourself only when driving the runtime manually (e.g. tests).</summary>
    void Update(float dt);
}

/// <summary>
/// Advances the active force agents at a fixed 25 Hz (dt = 0.04 s), independent of the engine
/// frame rate: a fixed-mode <see cref="IWorldStepper"/> driven by the <see cref="WorldCoordinator"/>
/// (on the world step, after the frame's next-tick queue, cycles and effects in inline mode).
/// Singleton, registered whenever <c>altruist:game</c> exists (also in 3D mode). Its 25 Hz step is
/// independent of the 2D world organizer's variable-rate physics step.
/// </summary>
[Service(typeof(IForceRuntime2D))]
[Service(typeof(IWorldStepper))]
[ConditionalOnConfig("altruist:game")]
public sealed class ForceRuntime2D : IForceRuntime2D, IWorldStepper
{
    private const int TickHz = 25;

    /// <summary>Always <see cref="StepMode.Fixed"/>.</summary>
    public StepMode Mode => StepMode.Fixed;
    /// <summary>25 steps per second.</summary>
    public int FixedHz => TickHz;
    /// <summary>Coordinator hook: <see cref="Update"/> with the fixed dt (0.04 s).</summary>
    public void FixedStep(in FixedStep step) => Update(step.Dt);

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ForceAgent2D, byte> _agents = new();

    /// <summary>DI constructor.</summary>
    public ForceRuntime2D(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<ForceRuntime2D>();
    }

    /// <inheritdoc/>
    public int ActiveCount => _agents.Count;

    /// <inheritdoc/>
    public ForceAgent2D ApplySustained(IPhysxBody2D body, Vector2 worldVelocity, float durationSeconds)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (durationSeconds <= 0f) throw new ArgumentException("duration must be > 0", nameof(durationSeconds));

        var agent = new ForceAgent2D(body, worldVelocity, durationSeconds);
        _agents.TryAdd(agent, 0);
        return agent;
    }

    /// <inheritdoc/>
    public ForceAgent2D ApplySustained(IPhysxBody2D body, Vector2 direction, float speed, float durationSeconds)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (durationSeconds <= 0f) throw new ArgumentException("duration must be > 0", nameof(durationSeconds));

        if (direction.LengthSquared() < 1e-12f || speed <= 0f)
        {
            var noop = new ForceAgent2D(body, Vector2.Zero, durationSeconds);
            noop.Cancel();
            return noop;
        }

        var unit = Vector2.Normalize(direction);
        return ApplySustained(body, unit * speed, durationSeconds);
    }

    /// <inheritdoc/>
    public void Cancel(ForceAgent2D agent)
    {
        if (agent == null) return;
        agent.Cancel();
        if (_agents.TryRemove(agent, out _))
            FireExpired(agent);
    }

    /// <inheritdoc/>
    public void CancelAll(IPhysxBody2D body)
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

    /// <summary>Advances every agent by <paramref name="dt"/> seconds (no-op for non-positive dt); removes
    /// finished agents and fires their <c>OnExpired</c>. An agent whose tick throws is logged and removed.</summary>
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
                _logger.LogError(ex, "[ForceRuntime2D] agent tick threw — removing it.");
                if (_agents.TryRemove(agent, out _))
                    FireExpired(agent);
            }
        }
    }

    private void FireExpired(ForceAgent2D agent)
    {
        try { agent.OnExpired?.Invoke(agent.Body); }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[ForceRuntime2D] OnExpired callback threw.");
        }
    }
}
