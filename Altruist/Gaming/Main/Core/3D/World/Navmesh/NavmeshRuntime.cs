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
    public NavMeshAgent(INavMeshService service, string zone, float replanIfTargetMovedBy = 1.5f)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _zone = zone ?? throw new ArgumentNullException(nameof(zone));
        _replanIfTargetMovedSq = replanIfTargetMovedBy * replanIfTargetMovedBy;
    }

    /// <summary>Body-bound constructor: an <see cref="INavMeshRuntime"/>
    /// drives motion. <paramref name="speed"/> is in world units per
    /// second (mobs and players typically run at 4–8).</summary>
    public NavMeshAgent(INavMeshService service, string zone, IPhysxBody3D body, float speed, float replanIfTargetMovedBy = 1.5f)
        : this(service, zone, replanIfTargetMovedBy)
    {
        Body = body ?? throw new ArgumentNullException(nameof(body));
        Speed = speed;
    }

    public NavPath CurrentPath => _path;
    public bool HasPath => _path.Waypoints.Count > 0 && _waypointIdx < _path.Waypoints.Count;

    /// <summary>The next world-space waypoint the agent is heading toward,
    /// or null if the path is exhausted.</summary>
    public Vector3? CurrentWaypoint
        => HasPath ? _path.Waypoints[_waypointIdx] : (Vector3?)null;

    /// <summary>Plan a path from <paramref name="from"/> to <paramref name="to"/>.
    /// Replaces any in-flight path. Returns true on success.</summary>
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
public interface INavMeshRuntime
{
    NavMeshAgent CreateAgent(string zone, IPhysxBody3D body, float speed, float replanIfTargetMovedBy = 1.5f);
    void RegisterAgent(NavMeshAgent agent);
    void RemoveAgent(NavMeshAgent agent);
    int RegisteredCount { get; }
    /// <summary>Tick every registered agent. Typically called once per
    /// world step from a <c>[PostStep]</c> service.</summary>
    void Update(float dt);
}

[Service(typeof(INavMeshRuntime))]
[ConditionalOnConfig("altruist:game")]
public sealed class NavMeshRuntime : INavMeshRuntime
{
    private readonly INavMeshService _navMesh;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<NavMeshAgent, byte> _agents = new();

    public NavMeshRuntime(INavMeshService navMesh, ILoggerFactory loggerFactory)
    {
        _navMesh = navMesh;
        _logger = loggerFactory.CreateLogger<NavMeshRuntime>();
    }

    public int RegisteredCount => _agents.Count;

    public NavMeshAgent CreateAgent(string zone, IPhysxBody3D body, float speed, float replanIfTargetMovedBy = 1.5f)
    {
        var agent = new NavMeshAgent(_navMesh, zone, body, speed, replanIfTargetMovedBy);
        RegisterAgent(agent);
        return agent;
    }

    public void RegisterAgent(NavMeshAgent agent)
    {
        if (agent == null) throw new ArgumentNullException(nameof(agent));
        if (agent.Body == null)
            throw new ArgumentException("Agent must be body-bound to register with the runtime; use the body+speed constructor.", nameof(agent));
        _agents.TryAdd(agent, 0);
    }

    public void RemoveAgent(NavMeshAgent agent)
    {
        if (agent == null) return;
        _agents.TryRemove(agent, out _);
        agent.Stop();
    }

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
