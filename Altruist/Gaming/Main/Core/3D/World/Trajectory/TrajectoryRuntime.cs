/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Numerics;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;
using Altruist.ThreeD.Numerics.Trajectory;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.ThreeD;

/// <summary>Per-launch handle: caller can read progress, cancel, or hook
/// the arrival callback. Created by <see cref="ITrajectoryRuntime"/>;
/// never instantiated directly.</summary>
public sealed class TrajectoryAgent
{
    private readonly Func<float, Vector3> _sampler;
    private readonly bool _snapY;
    private readonly string? _zone;

    internal TrajectoryAgent(IPhysxBody3D body, Func<float, Vector3> sampler,
                             float duration, bool snapYToNavMesh, string? zone)
    {
        Body = body;
        _sampler = sampler;
        Duration = duration;
        _snapY = snapYToNavMesh;
        _zone = zone;
        IsActive = true;
    }

    public IPhysxBody3D Body { get; }
    public float Elapsed { get; private set; }
    public float Duration { get; }
    public bool IsActive { get; private set; }

    /// <summary>Normalized progress in [0, 1]; reaches 1 only after the
    /// final tick that fires <see cref="OnArrive"/>.</summary>
    public float T => Duration > 0f ? MathF.Min(1f, Elapsed / Duration) : 1f;

    /// <summary>Invoked exactly once when the trajectory completes, after
    /// the final position is written. Set by the caller after
    /// <c>LaunchParabolic</c>/<c>LaunchCurve</c>; never reassigned by the runtime.</summary>
    public Action<IPhysxBody3D>? OnArrive { get; set; }

    public void Cancel()
    {
        IsActive = false;
    }

    /// <summary>Called by the runtime each tick. Returns true while still
    /// active; false when finished (caller should evict).</summary>
    internal bool Tick(float dt, INavMeshService? navMesh)
    {
        if (!IsActive) return false;

        Elapsed += dt;
        var t = T;
        var pos = _sampler(t);

        if (_snapY && navMesh != null && _zone != null
            && navMesh.TrySamplePosition(_zone, pos, maxDistance: 4f, out var snapped))
        {
            pos = new Vector3(pos.X, snapped.Y, pos.Z);
        }

        Body.Position = pos;

        if (Elapsed >= Duration)
        {
            IsActive = false;
            OnArrive?.Invoke(Body);
            return false;
        }
        return true;
    }
}

/// <summary>Central registry + ticker for in-flight kinematic trajectories.
/// Mirror of <see cref="INavMeshRuntime"/>: register with
/// <c>LaunchParabolic</c> / <c>LaunchCurve</c>, the runtime ticks every
/// active agent each frame and writes <see cref="IPhysxBody3D.Position"/>.
/// Use for charge dashes, knockback flights, fixed-arc projectiles —
/// anywhere the body follows a precomputed parametric path rather than
/// physics or A*.</summary>
public interface ITrajectoryRuntime
{
    TrajectoryAgent LaunchParabolic(IPhysxBody3D body, Vector3 start, Vector3 end,
                                    float peakHeight, float durationSeconds,
                                    float riseEndN = 0.28f, float hangEndN = 0.55f,
                                    bool snapYToNavMesh = false, string? zone = null);

    TrajectoryAgent LaunchCurve(IPhysxBody3D body, Func<float, Vector3> sampler,
                                float durationSeconds,
                                bool snapYToNavMesh = false, string? zone = null);

    void Cancel(TrajectoryAgent agent);
    int  ActiveCount { get; }
    void Update(float dt);
}

[Service(typeof(ITrajectoryRuntime))]
[ConditionalOnConfig("altruist:game")]
public sealed class TrajectoryRuntime : ITrajectoryRuntime
{
    private readonly INavMeshService _navMesh;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<TrajectoryAgent, byte> _agents = new();

    public TrajectoryRuntime(INavMeshService navMesh, ILoggerFactory loggerFactory)
    {
        _navMesh = navMesh ?? throw new ArgumentNullException(nameof(navMesh));
        _logger = loggerFactory.CreateLogger<TrajectoryRuntime>();
    }

    public int ActiveCount => _agents.Count;

    public TrajectoryAgent LaunchParabolic(IPhysxBody3D body, Vector3 start, Vector3 end,
                                           float peakHeight, float durationSeconds,
                                           float riseEndN = 0.28f, float hangEndN = 0.55f,
                                           bool snapYToNavMesh = false, string? zone = null)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (durationSeconds <= 0f) throw new ArgumentException("duration must be > 0", nameof(durationSeconds));
        if (snapYToNavMesh && zone == null) throw new ArgumentException("zone is required when snapYToNavMesh is true", nameof(zone));

        Vector3 Sampler(float t) => Trajectory.ParabolicSample(start, end, t, peakHeight, riseEndN, hangEndN);
        var agent = new TrajectoryAgent(body, Sampler, durationSeconds, snapYToNavMesh, zone);
        _agents.TryAdd(agent, 0);
        return agent;
    }

    public TrajectoryAgent LaunchCurve(IPhysxBody3D body, Func<float, Vector3> sampler,
                                       float durationSeconds,
                                       bool snapYToNavMesh = false, string? zone = null)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (sampler == null) throw new ArgumentNullException(nameof(sampler));
        if (durationSeconds <= 0f) throw new ArgumentException("duration must be > 0", nameof(durationSeconds));
        if (snapYToNavMesh && zone == null) throw new ArgumentException("zone is required when snapYToNavMesh is true", nameof(zone));

        var agent = new TrajectoryAgent(body, sampler, durationSeconds, snapYToNavMesh, zone);
        _agents.TryAdd(agent, 0);
        return agent;
    }

    public void Cancel(TrajectoryAgent agent)
    {
        if (agent == null) return;
        agent.Cancel();
        _agents.TryRemove(agent, out _);
    }

    public void Update(float dt)
    {
        if (dt <= 0f || _agents.IsEmpty) return;
        foreach (var kv in _agents)
        {
            var agent = kv.Key;
            try
            {
                if (!agent.Tick(dt, _navMesh))
                    _agents.TryRemove(agent, out _);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[TrajectoryRuntime] agent tick threw — removing it.");
                _agents.TryRemove(agent, out _);
            }
        }
    }
}
