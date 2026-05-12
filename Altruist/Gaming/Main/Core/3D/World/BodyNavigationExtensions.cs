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
public static class BodyNavigationExtensions3D
{
    // ── Sugar overloads on world objects ─────────────────────────────────

    public static void FaceToward(this IPhysxBody3D body, IWorldObject3D target)
        => body.FaceToward(target.Transform.Position);

    public static float HorizontalDistanceTo(this IPhysxBody3D body, IWorldObject3D target)
        => body.HorizontalDistanceTo(target.Transform.Position);

    public static float DistanceTo(this IPhysxBody3D body, IWorldObject3D target)
        => body.DistanceTo(target.Transform.Position);

    public static bool IsFacing(this IPhysxBody3D body, IWorldObject3D target, float halfAngleDegrees)
        => body.IsFacing(target.Transform.Position, halfAngleDegrees);

    public static void TurnToward(this IPhysxBody3D body, IWorldObject3D target,
                                  float maxAngularSpeedRadPerSec, float dt)
        => body.TurnToward(target.Transform.Position, maxAngularSpeedRadPerSec, dt);

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
    public static void MoveToward(this IPhysxBody3D body, Position3D worldPoint,
                                  float speed, float dt, ITerrainProvider terrain)
        => MoveToward(body, worldPoint.ToVector3(), speed, dt, terrain);
    public static void MoveToward(this IPhysxBody3D body, IWorldObject3D target,
                                  float speed, float dt, ITerrainProvider terrain)
        => MoveToward(body, target.Transform.Position.ToVector3(), speed, dt, terrain);

    /// <summary>Step the body in direction <paramref name="yawRadians"/> at
    /// <paramref name="speed"/> for one tick of <paramref name="dt"/>, then
    /// snap Y onto <paramref name="terrain"/>. Useful for wander / patrol
    /// where the AI picks a direction without a concrete target.</summary>
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
    public static NavMeshAgent? LaunchAlong(this IPhysxBody3D body, INavMeshRuntime runtime,
                                            string zone, Position3D destination, float speed)
        => LaunchAlong(body, runtime, zone, destination.ToVector3(), speed);

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
    public static TrajectoryAgent3D LaunchTrajectory(this IPhysxBody3D body,
                                                   ITrajectoryRuntime3D runtime,
                                                   Position3D start, Position3D end,
                                                   float peakHeight, float durationSeconds,
                                                   bool snapYToNavMesh = false, string? zone = null)
        => LaunchTrajectory(body, runtime, start.ToVector3(), end.ToVector3(), peakHeight,
                            durationSeconds, snapYToNavMesh, zone);
}
