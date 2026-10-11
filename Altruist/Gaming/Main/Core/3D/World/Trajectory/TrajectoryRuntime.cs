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
/// the arrival callback. Created by <see cref="ITrajectoryRuntime3D"/>;
/// never instantiated directly.</summary>
public sealed class TrajectoryAgent3D
{
    private readonly Func<float, Vector3> _sampler;
    private readonly bool _snapY;
    private readonly string? _zone;

    internal TrajectoryAgent3D(IPhysxBody3D body, Func<float, Vector3> sampler,
                               float duration, bool snapYToNavMesh, string? zone)
    {
        Body = body;
        _sampler = sampler;
        Duration = duration;
        _snapY = snapYToNavMesh;
        _zone = zone;
        IsActive = true;
    }

    /// <summary>The body whose <c>Position</c> is written each tick.</summary>
    public IPhysxBody3D Body { get; }
    /// <summary>Seconds simulated since launch.</summary>
    public float Elapsed { get; private set; }
    /// <summary>Total flight time in seconds.</summary>
    public float Duration { get; }
    /// <summary><c>false</c> once the trajectory finished or was cancelled.</summary>
    public bool IsActive { get; private set; }

    /// <summary>Normalized progress in [0, 1]; reaches 1 only after the
    /// final tick that fires <see cref="OnArrive"/>.</summary>
    public float T => Duration > 0f ? MathF.Min(1f, Elapsed / Duration) : 1f;

    /// <summary>Invoked exactly once when the trajectory completes, after
    /// the final position is written. Set by the caller after
    /// <c>LaunchParabolic</c>/<c>LaunchCurve</c>; never reassigned by the runtime.</summary>
    public Action<IPhysxBody3D>? OnArrive { get; set; }

    /// <summary>
    /// Stops the trajectory where it is (the body keeps its last written position; <see cref="OnArrive"/> is not raised).
    /// The runtime evicts it on its next update; prefer <see cref="ITrajectoryRuntime3D.Cancel"/> to evict immediately.
    /// </summary>
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
/// <remarks>
/// <para>Choose by motion source: trajectories for a fixed, precomputed path over a fixed duration;
/// <see cref="IForceRuntime3D"/> for force/velocity-driven motion that reacts to forces; <see cref="INavMeshRuntime"/> for
/// walking along navmesh paths. The body should be kinematic (or otherwise not simulated) while flying, since its position is
/// overwritten every update.</para>
/// <para>Nothing in the framework calls <see cref="Update"/>: drive it once per tick from your own stepper or game loop.</para>
/// </remarks>
/// <example>
/// <code>
/// var agent = trajectories.LaunchParabolic(body, from, to, peakHeight: 2f, durationSeconds: 0.6f);
/// agent.OnArrive = b =&gt; OnLanded(b);
/// // each tick:
/// trajectories.Update(dt);
/// </code>
/// </example>
public interface ITrajectoryRuntime3D
{
    /// <summary>
    /// Launches <paramref name="body"/> along a three-phase arc (ease-out rise, flat hang, ease-in fall) from
    /// <paramref name="start"/> to <paramref name="end"/>: XZ and the base Y are lerped linearly over time and the arc height is added on top.
    /// </summary>
    /// <param name="body">Body to move.</param>
    /// <param name="start">Start point in world space.</param>
    /// <param name="end">End point in world space.</param>
    /// <param name="peakHeight">Height added above the straight line at the hang phase, in world units (≤ 0 = straight line).</param>
    /// <param name="durationSeconds">Flight time in seconds (must be &gt; 0).</param>
    /// <param name="riseEndN">Normalized time [0, 1] at which the rise ends.</param>
    /// <param name="hangEndN">Normalized time [0, 1] at which the fall starts.</param>
    /// <param name="snapYToNavMesh">Snap Y to the navmesh of <paramref name="zone"/> (within 4 units) every tick.</param>
    /// <param name="zone">Navmesh zone; required when <paramref name="snapYToNavMesh"/> is <c>true</c>.</param>
    /// <returns>The registered agent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Non-positive duration, or snapping requested without a zone.</exception>
    TrajectoryAgent3D LaunchParabolic(IPhysxBody3D body, Vector3 start, Vector3 end,
                                      float peakHeight, float durationSeconds,
                                      float riseEndN = 0.28f, float hangEndN = 0.55f,
                                      bool snapYToNavMesh = false, string? zone = null);

    /// <summary>Launches <paramref name="body"/> along a caller-supplied curve sampled by normalized time.</summary>
    /// <param name="body">Body to move.</param>
    /// <param name="sampler">Maps normalized time t ∈ [0, 1] to a world position; called once per update.</param>
    /// <param name="durationSeconds">Flight time in seconds (must be &gt; 0).</param>
    /// <param name="snapYToNavMesh">Snap Y to the navmesh of <paramref name="zone"/> (within 4 units) every tick.</param>
    /// <param name="zone">Navmesh zone; required when <paramref name="snapYToNavMesh"/> is <c>true</c>.</param>
    /// <returns>The registered agent.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="body"/> or <paramref name="sampler"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">Non-positive duration, or snapping requested without a zone.</exception>
    TrajectoryAgent3D LaunchCurve(IPhysxBody3D body, Func<float, Vector3> sampler,
                                  float durationSeconds,
                                  bool snapYToNavMesh = false, string? zone = null);

    /// <summary>Cancels and immediately removes an agent (no arrival callback). <c>null</c> is ignored.</summary>
    /// <param name="agent">The agent to cancel.</param>
    void Cancel(TrajectoryAgent3D agent);
    /// <summary>Number of agents still registered.</summary>
    int  ActiveCount { get; }
    /// <summary>
    /// Advances every active agent by <paramref name="dt"/>, writes body positions, raises arrival callbacks and evicts finished
    /// agents. Agents whose tick throws are logged and removed. Iteration order over agents is unspecified.
    /// </summary>
    /// <param name="dt">Step length in seconds; ≤ 0 is a no-op.</param>
    void Update(float dt);
}

/// <summary>
/// Default singleton <see cref="ITrajectoryRuntime3D"/> (registered when <c>altruist:game</c> is configured). Agents are kept
/// in a concurrent set, so launching from other threads is safe; <see cref="Update"/> should run on one thread.
/// </summary>
[Service(typeof(ITrajectoryRuntime3D))]
[ConditionalOnConfig("altruist:game")]
public sealed class TrajectoryRuntime3D : ITrajectoryRuntime3D
{
    private readonly INavMeshService _navMesh;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<TrajectoryAgent3D, byte> _agents = new();

    /// <summary>Creates the runtime.</summary>
    /// <param name="navMesh">Navmesh service used for optional Y snapping.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    /// <exception cref="ArgumentNullException"><paramref name="navMesh"/> is <c>null</c>.</exception>
    public TrajectoryRuntime3D(INavMeshService navMesh, ILoggerFactory loggerFactory)
    {
        _navMesh = navMesh ?? throw new ArgumentNullException(nameof(navMesh));
        _logger = loggerFactory.CreateLogger<TrajectoryRuntime3D>();
    }

    /// <inheritdoc/>
    public int ActiveCount => _agents.Count;

    /// <inheritdoc/>
    public TrajectoryAgent3D LaunchParabolic(IPhysxBody3D body, Vector3 start, Vector3 end,
                                             float peakHeight, float durationSeconds,
                                             float riseEndN = 0.28f, float hangEndN = 0.55f,
                                             bool snapYToNavMesh = false, string? zone = null)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (durationSeconds <= 0f) throw new ArgumentException("duration must be > 0", nameof(durationSeconds));
        if (snapYToNavMesh && zone == null) throw new ArgumentException("zone is required when snapYToNavMesh is true", nameof(zone));

        Vector3 Sampler(float t) => Trajectory3D.ParabolicSample(start, end, t, peakHeight, riseEndN, hangEndN);
        var agent = new TrajectoryAgent3D(body, Sampler, durationSeconds, snapYToNavMesh, zone);
        _agents.TryAdd(agent, 0);
        return agent;
    }

    /// <inheritdoc/>
    public TrajectoryAgent3D LaunchCurve(IPhysxBody3D body, Func<float, Vector3> sampler,
                                         float durationSeconds,
                                         bool snapYToNavMesh = false, string? zone = null)
    {
        if (body == null) throw new ArgumentNullException(nameof(body));
        if (sampler == null) throw new ArgumentNullException(nameof(sampler));
        if (durationSeconds <= 0f) throw new ArgumentException("duration must be > 0", nameof(durationSeconds));
        if (snapYToNavMesh && zone == null) throw new ArgumentException("zone is required when snapYToNavMesh is true", nameof(zone));

        var agent = new TrajectoryAgent3D(body, sampler, durationSeconds, snapYToNavMesh, zone);
        _agents.TryAdd(agent, 0);
        return agent;
    }

    /// <inheritdoc/>
    public void Cancel(TrajectoryAgent3D agent)
    {
        if (agent == null) return;
        agent.Cancel();
        _agents.TryRemove(agent, out _);
    }

    /// <inheritdoc/>
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
                _logger.LogError(ex, "[TrajectoryRuntime3D] agent tick threw — removing it.");
                _agents.TryRemove(agent, out _);
            }
        }
    }
}
