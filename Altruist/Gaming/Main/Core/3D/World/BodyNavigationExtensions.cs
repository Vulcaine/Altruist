/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>Body verbs that need access to higher-level Gaming types
/// (<see cref="IWorldObject3D"/>, <see cref="INavMeshRuntime"/>,
/// <see cref="ITrajectoryRuntime"/>). Lives in Gaming because the pure
/// <see cref="BodySteeringExtensions"/> in Physx can't reference these.</summary>
public static class BodyNavigationExtensions
{
    // ── Sugar overloads on world objects ─────────────────────────────────

    public static void FaceToward(this IPhysxBody3D body, IWorldObject3D target)
        => body.FaceToward(target.Transform.Position.ToVector3());

    public static float HorizontalDistanceTo(this IPhysxBody3D body, IWorldObject3D target)
        => body.HorizontalDistanceTo(target.Transform.Position.ToVector3());

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
    public static TrajectoryAgent LaunchTrajectory(this IPhysxBody3D body,
                                                   ITrajectoryRuntime runtime,
                                                   Vector3 start, Vector3 end,
                                                   float peakHeight, float durationSeconds,
                                                   bool snapYToNavMesh = false, string? zone = null)
    {
        if (runtime == null) throw new ArgumentNullException(nameof(runtime));
        return runtime.LaunchParabolic(body, start, end, peakHeight, durationSeconds,
                                       snapYToNavMesh: snapYToNavMesh, zone: zone);
    }
}
