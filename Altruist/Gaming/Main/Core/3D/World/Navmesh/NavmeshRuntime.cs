/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Collections.Concurrent;
using System.Numerics;
using Altruist.Physx.ThreeD;
using Microsoft.Extensions.Logging;

namespace Altruist.Gaming.ThreeD;

/// <summary>"Follow the path" runtime for an entity. Holds a current path,
/// advances along it, and tracks whether re-planning is needed (target
/// moved past a tolerance). Stateful per-entity; owners (mob AI, player
/// click-to-move, charge skill) create one and tick it each frame.
///
/// Two usage modes:
/// 1. <b>Manual.</b> Construct with just the service + zone; the caller
///    drives motion (reads <see cref="CurrentWaypoint"/>, moves the body
///    however it wants, calls <see cref="StepToward"/> on arrival). This
///    is what the charge skill uses — it has its own KCC-driven motion
///    pipeline and just needs the path waypoints.
/// 2. <b>Body-bound.</b> Construct with a physics body + speed; pass the
///    agent to an <see cref="INavMeshRuntime"/> via <see cref="INavMeshRuntime.RegisterAgent"/>.
///    The runtime ticks every registered agent each frame, sets
///    <c>body.LinearVelocity</c> in the direction of the next waypoint,
///    and handles arrival/replan automatically. Right shape for mob AI.</summary>
/// <remarks>
/// Not thread-safe; use one agent per entity. Convenience: <see cref="BodyNavigationExtensions3D.LaunchAlong(IPhysxBody3D, INavMeshRuntime, string, Vector3, float)"/>
/// creates, registers and starts a body-bound agent in one call. A body-bound agent whose path is finished stays registered and keeps
/// writing zero velocity each tick until removed with <see cref="INavMeshRuntime.RemoveAgent"/>.
/// </remarks>
/// <example>
/// <code>
/// var agent = navRuntime.CreateAgent("zone-1", mobBody, speed: 5f);
/// agent.SetDestination(mobBody.Position, goal);
/// // per tick (your stepper): navRuntime.Update(dt);
/// // chasing: agent.TrackTarget(mobBody.Position, target.Transform.Position.ToVector3());
/// </code>
/// </example>
public sealed class NavMeshAgent
{
    private readonly INavMeshService _service;
    private readonly string _zone;

    private NavPath _path = NavPath.Empty;
    private int _waypointIdx;
    private Vector3 _lastDestination;
    private float _replanIfTargetMovedSq;

    /// <summary>Optional physics body the runtime drives. Null in manual
    /// mode.</summary>
    public IPhysxBody3D? Body { get; }
    /// <summary>Movement speed in world units per second; only meaningful
    /// when <see cref="Body"/> is set.</summary>
    public float Speed { get; set; }
    /// <summary>How close the agent has to be to the current waypoint
    /// before advancing the cursor. Body-bound agents that aren't quite
    /// arriving (oscillating around the waypoint) usually want this raised.</summary>
    public float ArrivalRadius { get; set; } = 0.4f;

    /// <summary>Manual-mode constructor: caller drives motion, agent just
    /// holds path state.</summary>
    /// <param name="service">Navmesh service used for path queries.</param>
    /// <param name="zone">Navmesh zone name.</param>
    /// <param name="replanIfTargetMovedBy">XZ distance (world units) the target must move before <see cref="TrackTarget"/> re-plans.</param>
    /// <exception cref="ArgumentNullException"><paramref name="service"/> or <paramref name="zone"/> is <c>null</c>.</exception>
    public NavMeshAgent(INavMeshService service, string zone, float replanIfTargetMovedBy = 1.5f)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _replanIfTargetMovedSq = replanIfTargetMovedBy * replanIfTargetMovedBy;
    }

    /// <summary>Body-bound constructor: an <see cref="INavMeshRuntime"/>
    /// drives motion. <paramref name="speed"/> is in world units per
    /// second (mobs and players typically run at 4–8).</summary>
    /// <param name="service">Navmesh service used for path queries.</param>
    /// <param name="zone">Navmesh zone name.</param>
    /// <param name="body">Body whose velocity the runtime drives.</param>
    /// <param name="speed">Speed in world units per second.</param>
    /// <param name="replanIfTargetMovedBy">XZ distance the target must move before <see cref="TrackTarget"/> re-plans.</param>
    /// <exception cref="ArgumentNullException">Any reference argument is <c>null</c>.</exception>
    public NavMeshAgent(INavMeshService service, string zone, IPhysxBody3D body, float speed, float replanIfTargetMovedBy = 1.5f)
        : this(service, zone, replanIfTargetMovedBy)
    {
        Body = body ?? throw new ArgumentNullException(nameof(body));
        Speed = speed;
    }

    /// <summary>The current path (<see cref="NavPath.Empty"/> when none).</summary>
    public NavPath CurrentPath => _path;
    /// <summary><c>true</c> while waypoints remain to be reached.</summary>
    public bool HasPath => _path.Waypoints.Count > 0 && _waypointIdx < _path.Waypoints.Count;

    /// <summary>The next world-space waypoint the agent is heading toward,
    /// or null if the path is exhausted.</summary>
    public Vector3? CurrentWaypoint
        => HasPath ? _path.Waypoints[_waypointIdx] : (Vector3?)null;

    /// <summary>Plan a path from <paramref name="from"/> to <paramref name="to"/>.
    /// Replaces any in-flight path. Returns true on success.</summary>
    /// <remarks>Uses <see cref="INavMeshService.FindPath"/> with the default 4-unit snap distance; the first waypoint (the snapped start) is skipped.</remarks>
    /// <param name="from">Current position.</param>
    /// <param name="to">Destination.</param>
    /// <returns><c>false</c> (and the path cleared) when no path was found.</returns>
    public bool SetDestination(Vector3 from, Vector3 to)
    {
        var fresh = _service.FindPath(_zone, from, to);
        if (fresh.Waypoints.Count == 0)
        {
            _path = NavPath.Empty;
            _waypointIdx = 0;
            return false;
        }
        _path = fresh;
        _waypointIdx = 1; // 0 is the start point itself; first goal is 1
        _lastDestination = to;
        return true;
    }

    /// <summary>Tracking variant: re-plans automatically when the target
    /// has moved more than the agent's replan tolerance since the last
    /// path. Cheap when the target is still — just a squared-distance
    /// compare.</summary>
    /// <param name="from">Current position (used only when re-planning).</param>
    /// <param name="movingTarget">Current target position.</param>
    /// <returns><c>true</c> when a path exists after the call.</returns>
    public bool TrackTarget(Vector3 from, Vector3 movingTarget)
    {
        if (!HasPath)
            return SetDestination(from, movingTarget);

        float dx = movingTarget.X - _lastDestination.X;
        float dz = movingTarget.Z - _lastDestination.Z;
        if (dx * dx + dz * dz > _replanIfTargetMovedSq)
            return SetDestination(from, movingTarget);

        return true;
    }

    /// <summary>Advance the waypoint cursor if the agent is within
    /// <paramref name="arrivalRadius"/> of the current waypoint. Returns
    /// the position the agent should head toward this frame (current
    /// waypoint), or null if the path is fully consumed.</summary>
    /// <remarks>Distance is measured on the XZ plane; at most one waypoint is consumed per call.</remarks>
    /// <param name="currentPosition">The agent's current position.</param>
    /// <param name="arrivalRadius">XZ radius within which the current waypoint counts as reached.</param>
    public Vector3? StepToward(Vector3 currentPosition, float arrivalRadius = 0.4f)
    {
        if (!HasPath) return null;

        var target = _path.Waypoints[_waypointIdx];
        float dx = target.X - currentPosition.X;
        float dz = target.Z - currentPosition.Z;
        if (dx * dx + dz * dz <= arrivalRadius * arrivalRadius)
        {
            _waypointIdx++;
            if (!HasPath) return null;
            target = _path.Waypoints[_waypointIdx];
        }
        return target;
    }

    /// <summary>Clears the path and, for body-bound agents, sets the body's velocity to zero (all axes). Does not unregister the agent.</summary>
    public void Stop()
    {
        _path = NavPath.Empty;
        _waypointIdx = 0;
        if (Body != null)
            Body.LinearVelocity = Vector3.Zero;
    }

    /// <summary>Body-bound tick: read body position, advance toward next
    /// waypoint at <see cref="Speed"/>, set body velocity, advance cursor
    /// on arrival. Called by <see cref="INavMeshRuntime.Update"/> for every
    /// registered agent. No-op on manual-mode agents (no body).</summary>
    /// <remarks>Without a path or with <see cref="Speed"/> ≤ 0 the whole velocity (including Y) is set to zero.</remarks>
    /// <param name="dt">Step length in seconds (passed through; velocity is set in units per second).</param>
    public void Tick(float dt)
    {
        if (Body == null) return;
        if (!HasPath || Speed <= 0f)
        {
            Body.LinearVelocity = Vector3.Zero;
            return;
        }

        var next = StepToward(Body.Position, ArrivalRadius);
        if (next == null)
        {
            Body.LinearVelocity = Vector3.Zero;
            return;
        }

        // Y component is left at the body's current Y velocity so gravity /
        // kinematic falls aren't clobbered.
        Body.MoveToward(next.Value, Speed, dt);
    }
}

/// <summary>Central registry + ticker for body-bound nav-mesh agents.
/// Games that want full automation (mob AI, click-to-move, patrol)
/// register their agents here and call <see cref="Update"/> once per
/// game tick — the runtime walks every agent, advances paths, and writes
/// to the bound physics bodies. Manual-mode agents (the charge skill
/// driving its own motion) skip this entirely.</summary>
/// <remarks>
/// Use this for walking along navmesh paths; for precomputed arcs use <see cref="ITrajectoryRuntime3D"/>, for velocity pushes
/// <see cref="IForceRuntime3D"/>. Nothing in the framework calls <see cref="Update"/>: call it from your own stepper or tick code.
/// </remarks>
public interface INavMeshRuntime
{
    /// <summary>Creates a body-bound <see cref="NavMeshAgent"/> and registers it (no destination yet).</summary>
    /// <param name="zone">Navmesh zone name.</param>
    /// <param name="body">Body to drive.</param>
    /// <param name="speed">Speed in world units per second.</param>
    /// <param name="replanIfTargetMovedBy">XZ distance the target must move before <see cref="NavMeshAgent.TrackTarget"/> re-plans.</param>
    /// <returns>The registered agent.</returns>
    NavMeshAgent CreateAgent(string zone, IPhysxBody3D body, float speed, float replanIfTargetMovedBy = 1.5f);
    /// <summary>Registers an existing body-bound agent so <see cref="Update"/> ticks it (idempotent).</summary>
    /// <param name="agent">The agent.</param>
    /// <exception cref="ArgumentNullException"><paramref name="agent"/> is <c>null</c>.</exception>
    /// <exception cref="ArgumentException">The agent has no body (manual mode).</exception>
    void RegisterAgent(NavMeshAgent agent);
    /// <summary>Unregisters the agent and calls <see cref="NavMeshAgent.Stop"/> on it (zeroing its body velocity). <c>null</c> is ignored.</summary>
    /// <param name="agent">The agent.</param>
    void RemoveAgent(NavMeshAgent agent);
    /// <summary>Number of registered agents.</summary>
    int RegisteredCount { get; }
    /// <summary>Tick every registered agent. Call it once per world step from your own
    /// <see cref="IWorldStepper"/> or tick code; nothing calls it automatically.</summary>
    /// <remarks>Agents whose tick throws are logged and removed. Iteration order is unspecified.</remarks>
    /// <param name="dt">Step length in seconds; ≤ 0 is a no-op.</param>
    void Update(float dt);
}

/// <summary>Default singleton <see cref="INavMeshRuntime"/> (registered when <c>altruist:game</c> is configured); agents are kept in a concurrent set.</summary>
[Service(typeof(INavMeshRuntime))]
[ConditionalOnConfig("altruist:game")]
public sealed class NavMeshRuntime : INavMeshRuntime
{
    private readonly INavMeshService _navMesh;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<NavMeshAgent, byte> _agents = new();

    /// <summary>Creates the runtime.</summary>
    /// <param name="navMesh">Navmesh service passed to created agents.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public NavMeshRuntime(INavMeshService navMesh, ILoggerFactory loggerFactory)
    {
        _navMesh = navMesh;
        _logger = loggerFactory.CreateLogger<NavMeshRuntime>();
    }

    /// <inheritdoc/>
    public int RegisteredCount => _agents.Count;

    /// <inheritdoc/>
    public NavMeshAgent CreateAgent(string zone, IPhysxBody3D body, float speed, float replanIfTargetMovedBy = 1.5f)
    {
        var agent = new NavMeshAgent(_navMesh, zone, body, speed, replanIfTargetMovedBy);
        RegisterAgent(agent);
        return agent;
    }

    /// <inheritdoc/>
    public void RegisterAgent(NavMeshAgent agent)
    {
        if (agent == null) throw new ArgumentNullException(nameof(agent));
        if (agent.Body == null)
            throw new ArgumentException("Agent must be body-bound to register with the runtime; use the body+speed constructor.", nameof(agent));
        _agents.TryAdd(agent, 0);
    }

    /// <inheritdoc/>
    public void RemoveAgent(NavMeshAgent agent)
    {
        if (agent == null) return;
        _agents.TryRemove(agent, out _);
        agent.Stop();
    }

    /// <inheritdoc/>
    public void Update(float dt)
    {
        if (dt <= 0f || _agents.IsEmpty) return;
        // Snapshot to a flat array so concurrent register/remove during
        // tick (e.g. an AI behavior un-registers on death) doesn't fault
        // the enumerator. Iteration order doesn't matter — agents are
        // independent.
        foreach (var kv in _agents)
        {
            try
            {
                kv.Key.Tick(dt);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[NavMeshRuntime] agent tick threw — removing it.");
                _agents.TryRemove(kv.Key, out _);
            }
        }
    }
}
