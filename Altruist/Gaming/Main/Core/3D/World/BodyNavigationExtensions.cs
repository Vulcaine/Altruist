/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Numerics;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>Body verbs that need access to higher-level Gaming types
/// (<see cref="IWorldObject3D"/>, <see cref="INavMeshRuntime"/>,
/// <see cref="ITrajectoryRuntime3D"/>, <see cref="ITerrainProvider"/>). Lives
/// in Gaming because the pure <see cref="BodySteeringExtensions3D"/> in Physx
/// can't reference these.</summary>
/// <remarks>
/// Pick by body kind: velocity-driven bodies (character controller / dynamic) use the plain overloads, which forward to
/// <see cref="BodySteeringExtensions3D"/> and only set velocity; kinematic bodies without a controller use the
/// <see cref="ITerrainProvider"/> overloads, which write <c>Position</c> directly and snap Y to the terrain. For multi-waypoint
/// pathing use <see cref="LaunchAlong(IPhysxBody3D, INavMeshRuntime, string, Vector3, float)"/>; for jumps/dashes along an arc use
/// <see cref="LaunchTrajectory(IPhysxBody3D, ITrajectoryRuntime3D, Vector3, Vector3, float, float, bool, string)"/>.
/// Conventions: yaw in radians (0 = +Z, positive toward +X), +Y up, speeds in world units per second, <c>dt</c> in seconds.
/// For 2D see <see cref="Altruist.Gaming.TwoD.BodyNavigationExtensions2D"/>.
/// </remarks>
/// <example>
/// <code>
/// body.TurnToward(target, maxAngularSpeedRadPerSec: MathF.PI, dt);
/// if (body.IsFacing(target, halfAngleDegrees: 30f)) body.MoveToward(target, speed: 4f, dt);
/// // kinematic mob on a heightmap:
/// body.MoveToward(target.Transform.Position, 3f, dt, terrain);
/// </code>
/// </example>
public static class BodyNavigationExtensions3D
{
    // ── Sugar overloads on world objects ─────────────────────────────────

    /// <summary>Instantly sets the body's yaw to face <paramref name="target"/>'s position; see <see cref="BodySteeringExtensions3D.FaceToward(IPhysxBody3D, Vector3)"/>.</summary>
    /// <param name="body">Body to rotate.</param>
    /// <param name="target">Object to face.</param>
    public static void FaceToward(this IPhysxBody3D body, IWorldObject3D target)
        => body.FaceToward(target.Transform.Position);

    /// <summary>XZ-plane distance from the body to <paramref name="target"/>'s position.</summary>
    /// <param name="body">Source body.</param>
    /// <param name="target">Target object.</param>
    /// <returns>Distance in world units, ignoring Y.</returns>
    public static float HorizontalDistanceTo(this IPhysxBody3D body, IWorldObject3D target)
        => body.HorizontalDistanceTo(target.Transform.Position);

    /// <summary>Full 3D distance from the body to <paramref name="target"/>'s position.</summary>
    /// <param name="body">Source body.</param>
    /// <param name="target">Target object.</param>
    /// <returns>Distance in world units.</returns>
    public static float DistanceTo(this IPhysxBody3D body, IWorldObject3D target)
        => body.DistanceTo(target.Transform.Position);

    /// <summary>Whether <paramref name="target"/> lies within a horizontal cone in front of the body; see <see cref="BodySteeringExtensions3D.IsFacing(IPhysxBody3D, Vector3, float)"/>.</summary>
    /// <param name="body">Source body.</param>
    /// <param name="target">Target object.</param>
    /// <param name="halfAngleDegrees">Half-angle of the cone in degrees.</param>
    public static bool IsFacing(this IPhysxBody3D body, IWorldObject3D target, float halfAngleDegrees)
        => body.IsFacing(target.Transform.Position, halfAngleDegrees);

    /// <summary>Rotates the body's yaw toward <paramref name="target"/> by at most <paramref name="maxAngularSpeedRadPerSec"/> × <paramref name="dt"/>.</summary>
    /// <param name="body">Body to rotate.</param>
    /// <param name="target">Object to turn toward.</param>
    /// <param name="maxAngularSpeedRadPerSec">Maximum turn rate in radians per second.</param>
    /// <param name="dt">Step length in seconds.</param>
    public static void TurnToward(this IPhysxBody3D body, IWorldObject3D target,
                                  float maxAngularSpeedRadPerSec, float dt)
        => body.TurnToward(target.Transform.Position, maxAngularSpeedRadPerSec, dt);

    /// <summary>
    /// Sets a horizontal velocity of <paramref name="speed"/> toward <paramref name="target"/> (Y velocity preserved); see
    /// <see cref="BodySteeringExtensions3D.MoveToward(IPhysxBody3D, Vector3, float, float)"/>. For kinematic bodies without a
    /// controller use the <see cref="ITerrainProvider"/> overload instead.
    /// </summary>
    /// <param name="body">Body to steer.</param>
    /// <param name="target">Object to move toward.</param>
    /// <param name="speed">Speed in world units per second.</param>
    /// <param name="dt">Unused (API symmetry).</param>
    public static void MoveToward(this IPhysxBody3D body, IWorldObject3D target, float speed, float dt)
        => body.MoveToward(target.Transform.Position, speed, dt);

    // ── Terrain-aware motion (position-write + Y-snap) ───────────────────
    //
    // For kinematic mob bodies without a KCC pipeline: write Position
    // directly, then snap Y onto the terrain so the body follows hills.
    // KCC-driven player bodies should NOT use these overloads — the
    // velocity-write overloads in BodySteeringExtensions feed KCC's
    // slope-aware sweep, which is already terrain-aware.

    /// <summary>Move the body horizontally toward <paramref name="worldPoint"/>
    /// at <paramref name="speed"/> for one tick of <paramref name="dt"/>,
    /// then snap Y onto <paramref name="terrain"/>. For kinematic bodies
    /// that don't have a KCC consuming their velocity.</summary>
    /// <remarks>
    /// With a <c>null</c> <paramref name="terrain"/> it falls back to the velocity-based overload. When already at the target
    /// horizontally or <paramref name="speed"/> ≤ 0 it calls <c>Stop()</c> (zeroes XZ velocity) and does not move. May overshoot
    /// the target when <c>speed * dt</c> exceeds the remaining distance.
    /// </remarks>
    /// <param name="body">Body whose position is written.</param>
    /// <param name="worldPoint">Target point (only X/Z are used for direction).</param>
    /// <param name="speed">Speed in world units per second.</param>
    /// <param name="dt">Step length in seconds.</param>
    /// <param name="terrain">Height source for the Y snap.</param>
    public static void MoveToward(this IPhysxBody3D body, Vector3 worldPoint,
                                  float speed, float dt, ITerrainProvider terrain)
    {
        if (terrain == null) { body.MoveToward(worldPoint, speed, dt); return; }
        var dir = Direction3D.Horizontal(body.Position, worldPoint);
        if (dir == Vector3.Zero || speed <= 0f) { body.Stop(); return; }
        var step = speed * dt;
        var p = body.Position;
        var nx = p.X + dir.X * step;
        var nz = p.Z + dir.Z * step;
        body.Position = new Vector3(nx, terrain.GetHeight(nx, nz), nz);
    }
    /// <summary><see cref="Position3D"/> overload of <see cref="MoveToward(IPhysxBody3D, Vector3, float, float, ITerrainProvider)"/>.</summary>
    /// <param name="body">Body whose position is written.</param>
    /// <param name="worldPoint">Target point.</param>
    /// <param name="speed">Speed in world units per second.</param>
    /// <param name="dt">Step length in seconds.</param>
    /// <param name="terrain">Height source for the Y snap.</param>
    public static void MoveToward(this IPhysxBody3D body, Position3D worldPoint,
                                  float speed, float dt, ITerrainProvider terrain)
        => MoveToward(body, worldPoint.ToVector3(), speed, dt, terrain);
    /// <summary>World-object overload of <see cref="MoveToward(IPhysxBody3D, Vector3, float, float, ITerrainProvider)"/>.</summary>
    /// <param name="body">Body whose position is written.</param>
    /// <param name="target">Object to move toward.</param>
    /// <param name="speed">Speed in world units per second.</param>
    /// <param name="dt">Step length in seconds.</param>
    /// <param name="terrain">Height source for the Y snap.</param>
    public static void MoveToward(this IPhysxBody3D body, IWorldObject3D target,
                                  float speed, float dt, ITerrainProvider terrain)
        => MoveToward(body, target.Transform.Position.ToVector3(), speed, dt, terrain);

    /// <summary>Step the body in direction <paramref name="yawRadians"/> at
    /// <paramref name="speed"/> for one tick of <paramref name="dt"/>, then
    /// snap Y onto <paramref name="terrain"/>. Useful for wander / patrol
    /// where the AI picks a direction without a concrete target.</summary>
    /// <remarks>Yaw 0 moves along +Z, positive yaw toward +X. A <c>null</c> <paramref name="terrain"/> falls back to the velocity-based overload.</remarks>
    /// <param name="body">Body whose position is written.</param>
    /// <param name="yawRadians">Heading in radians.</param>
    /// <param name="speed">Speed in world units per second.</param>
    /// <param name="dt">Step length in seconds.</param>
    /// <param name="terrain">Height source for the Y snap.</param>
    public static void MoveTowardAngle(this IPhysxBody3D body, float yawRadians,
                                       float speed, float dt, ITerrainProvider terrain)
    {
        if (terrain == null) { body.MoveTowardAngle(yawRadians, speed, dt); return; }
        if (speed <= 0f) { body.Stop(); return; }
        var dir = Direction3D.TowardAngle(yawRadians);
        var step = speed * dt;
        var p = body.Position;
        var nx = p.X + dir.X * step;
        var nz = p.Z + dir.Z * step;
        body.Position = new Vector3(nx, terrain.GetHeight(nx, nz), nz);
    }

    // ── Path-driven launch ────────────────────────────────────────────────

    /// <summary>Wrap "create body-bound NavMeshAgent + register + set destination"
    /// into one call. Returns the agent so the caller can <see cref="NavMeshAgent.Stop"/>,
    /// read <see cref="NavMeshAgent.HasPath"/>, or call
    /// <see cref="NavMeshAgent.TrackTarget"/> for moving targets. Returns
    /// <c>null</c> if no path could be found from the body's current position.</summary>
    /// <param name="body">Body the agent drives.</param>
    /// <param name="runtime">Navmesh runtime that steps the agent.</param>
    /// <param name="zone">Navmesh zone name to path on.</param>
    /// <param name="destination">Destination in world space.</param>
    /// <param name="speed">Agent speed in world units per second.</param>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is <c>null</c>.</exception>
    public static NavMeshAgent? LaunchAlong(this IPhysxBody3D body, INavMeshRuntime runtime,
                                            string zone, Vector3 destination, float speed)
    {
        if (runtime == null) throw new ArgumentNullException(nameof(runtime));
        var agent = runtime.CreateAgent(zone, body, speed);
        if (!agent.SetDestination(body.Position, destination))
        {
            runtime.RemoveAgent(agent);
            return null;
        }
        return agent;
    }
    /// <summary><see cref="Position3D"/> overload of <see cref="LaunchAlong(IPhysxBody3D, INavMeshRuntime, string, Vector3, float)"/>.</summary>
    /// <param name="body">Body the agent drives.</param>
    /// <param name="runtime">Navmesh runtime that steps the agent.</param>
    /// <param name="zone">Navmesh zone name.</param>
    /// <param name="destination">Destination in world space.</param>
    /// <param name="speed">Agent speed in world units per second.</param>
    public static NavMeshAgent? LaunchAlong(this IPhysxBody3D body, INavMeshRuntime runtime,
                                            string zone, Position3D destination, float speed)
        => LaunchAlong(body, runtime, zone, destination.ToVector3(), speed);

    /// <summary>
    /// Paths to <paramref name="target"/>'s current position (a one-off destination, not tracked). For moving targets call
    /// <see cref="NavMeshAgent.TrackTarget"/> on the returned agent.
    /// </summary>
    /// <param name="body">Body the agent drives.</param>
    /// <param name="runtime">Navmesh runtime that steps the agent.</param>
    /// <param name="zone">Navmesh zone name.</param>
    /// <param name="target">Object whose position is the destination.</param>
    /// <param name="speed">Agent speed in world units per second.</param>
    /// <returns>The registered agent, or <c>null</c> if no path was found.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="target"/> or <paramref name="runtime"/> is <c>null</c>.</exception>
    public static NavMeshAgent? LaunchToward(this IPhysxBody3D body, INavMeshRuntime runtime,
                                             string zone, IWorldObject3D target, float speed)
    {
        if (target == null) throw new ArgumentNullException(nameof(target));
        return body.LaunchAlong(runtime, zone, target.Transform.Position.ToVector3(), speed);
    }

    // ── Trajectory launch ────────────────────────────────────────────────

    /// <summary>Launch the body along a parabolic trajectory from
    /// <paramref name="start"/> to <paramref name="end"/>. With
    /// <paramref name="peakHeight"/> = 0 this collapses to a straight-line
    /// kinematic dash; with peak height &gt; 0 the body rises and falls
    /// through a 3-phase arc. Returns a handle for cancellation /
    /// arrival callback.</summary>
    /// <param name="body">Body to move (should be kinematic while the trajectory runs).</param>
    /// <param name="runtime">Trajectory runtime that steps the agent.</param>
    /// <param name="start">Start point in world space.</param>
    /// <param name="end">End point in world space.</param>
    /// <param name="peakHeight">Extra height added on top of the straight start-to-end line at the arc peak, in world units (0 = straight line).</param>
    /// <param name="durationSeconds">Flight time in seconds.</param>
    /// <param name="snapYToNavMesh">Snap the landing Y onto the navmesh of <paramref name="zone"/>.</param>
    /// <param name="zone">Navmesh zone used for the snap.</param>
    /// <returns>Handle for cancellation and arrival callbacks.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="runtime"/> is <c>null</c>.</exception>
    public static TrajectoryAgent3D LaunchTrajectory(this IPhysxBody3D body,
                                                   ITrajectoryRuntime3D runtime,
                                                   Vector3 start, Vector3 end,
                                                   float peakHeight, float durationSeconds,
                                                   bool snapYToNavMesh = false, string? zone = null)
    {
        if (runtime == null) throw new ArgumentNullException(nameof(runtime));
        return runtime.LaunchParabolic(body, start, end, peakHeight, durationSeconds,
                                       snapYToNavMesh: snapYToNavMesh, zone: zone);
    }
    /// <summary><see cref="Position3D"/> overload of <see cref="LaunchTrajectory(IPhysxBody3D, ITrajectoryRuntime3D, Vector3, Vector3, float, float, bool, string)"/>.</summary>
    /// <param name="body">Body to move.</param>
    /// <param name="runtime">Trajectory runtime that steps the agent.</param>
    /// <param name="start">Start point in world space.</param>
    /// <param name="end">End point in world space.</param>
    /// <param name="peakHeight">Arc height in world units (0 = straight line).</param>
    /// <param name="durationSeconds">Flight time in seconds.</param>
    /// <param name="snapYToNavMesh">Snap the landing Y onto the navmesh of <paramref name="zone"/>.</param>
    /// <param name="zone">Navmesh zone used for the snap.</param>
    public static TrajectoryAgent3D LaunchTrajectory(this IPhysxBody3D body,
                                                   ITrajectoryRuntime3D runtime,
                                                   Position3D start, Position3D end,
                                                   float peakHeight, float durationSeconds,
                                                   bool snapYToNavMesh = false, string? zone = null)
        => LaunchTrajectory(body, runtime, start.ToVector3(), end.ToVector3(), peakHeight,
                            durationSeconds, snapYToNavMesh, zone);
}
