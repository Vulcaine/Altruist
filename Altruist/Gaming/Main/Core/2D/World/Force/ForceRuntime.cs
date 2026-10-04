/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Numerics;
using Altruist.Physx.TwoD;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.TwoD;

/// <summary>2D mirror of <see cref="ForceAgent3D"/>. Force is full-planar
/// (X and Y both drive motion) — there is no preserved axis since 2D has
/// no "gravity axis" equivalent of 3D's Y.</summary>
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

    public IPhysxBody2D Body { get; }
    public Vector2 Velocity { get; }
    public float Duration { get; }
    public float Elapsed { get; private set; }
    public bool IsActive { get; private set; }

    public float T => Duration > 0f ? MathF.Min(1f, Elapsed / Duration) : 1f;

    public Action<IPhysxBody2D, Vector2>? OnApplied { get; set; }
    public Action<IPhysxBody2D>? OnExpired { get; set; }

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
/// plane (no preserved axis).</summary>
public interface IForceRuntime2D
{
    ForceAgent2D ApplySustained(IPhysxBody2D body, Vector2 worldVelocity, float durationSeconds);
    ForceAgent2D ApplySustained(IPhysxBody2D body, Vector2 direction, float speed, float durationSeconds);
    void Cancel(ForceAgent2D agent);
    void CancelAll(IPhysxBody2D body);
    int ActiveCount { get; }
    void Update(float dt);
}

/// <summary>
/// Advances the active force agents at a fixed 25 Hz (dt = 0.04 s), independent of the engine
/// frame rate: a fixed-mode <see cref="IWorldStepper"/> driven by the <see cref="WorldCoordinator"/>
/// (on the world step, after the frame's next-tick queue, cycles and effects in inline mode).
/// </summary>
[Service(typeof(IForceRuntime2D))]
[Service(typeof(IWorldStepper))]
[ConditionalOnConfig("altruist:game")]
public sealed class ForceRuntime2D : IForceRuntime2D, IWorldStepper
{
    private const int TickHz = 25;

    public StepMode Mode => StepMode.Fixed;
    public int FixedHz => TickHz;
    public void FixedStep(in FixedStep step) => Update(step.Dt);

    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<ForceAgent2D, byte> _agents = new();

    public ForceRuntime2D(ILoggerFactory loggerFactory)
    {
        _logger = loggerFactory.CreateLogger<ForceRuntime2D>();
    }

    public int ActiveCount => _agents.Count;

    public ForceAgent2D ApplySustained(IPhysxBody2D body, Vector2 worldVelocity, float durationSeconds)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (durationSeconds <= 0f) throw new ArgumentException("duration must be > 0", nameof(durationSeconds));

        var agent = new ForceAgent2D(body, worldVelocity, durationSeconds);
        _agents.TryAdd(agent, 0);
        return agent;
    }

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

    public void Cancel(ForceAgent2D agent)
    {
        if (agent == null) return;
        agent.Cancel();
        if (_agents.TryRemove(agent, out _))
            FireExpired(agent);
    }

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
