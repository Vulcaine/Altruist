using System.Numerics;

using Altruist.Physx.ThreeD;

namespace Altruist.Gaming.World.ThreeD;

public sealed class NavPath
{
    public IReadOnlyList<Vector3> Waypoints { get; }
    public bool IsComplete { get; }
    public float Length { get; }

    public NavPath(IReadOnlyList<Vector3> waypoints, bool isComplete)
    {
        Waypoints = waypoints;
        IsComplete = isComplete;
        Length = ComputeLength(waypoints);
    }

    private static float ComputeLength(IReadOnlyList<Vector3> pts)
    {
        if (pts.Count < 2)
            return 0f;
        float sum = 0f;
        for (int i = 1; i < pts.Count; i++)
        {
            sum += Vector3.Distance(pts[i - 1], pts[i]);
        }
        return sum;
    }
}

public sealed class NavAgent
{
    public Guid Id { get; } = Guid.NewGuid();

    public IPhysxBody3D Body { get; }   // <-- physics body
    public float Speed { get; internal set; }

    public NavPath? CurrentPath { get; internal set; }
    public int CurrentWaypointIndex { get; internal set; }

    public bool HasPath => CurrentPath is not null && CurrentPath.Waypoints.Count > 0;

    public Vector3 Position => Body.Position;

    public NavAgent(IPhysxBody3D body, float speed)
    {
        Body = body;
        Speed = speed;
    }
}
