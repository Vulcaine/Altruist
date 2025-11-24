// using System.Numerics;

// using Altruist.Physx.ThreeD;

// namespace Altruist.Gaming.World.ThreeD;

// public interface INavMeshRuntime
// {
//     NavAgent CreateAgent(IPhysxBody3D body, float speed);
//     void RemoveAgent(NavAgent agent);

//     /// <summary>Request a new path for an agent; returns false if path failed.</summary>
//     bool SetDestination(NavAgent agent, Vector3 destination);

//     /// <summary>Advance all agents by deltaTime seconds.</summary>
//     void Update(float deltaTime);
// }

// [Service(typeof(INavMeshRuntime))]
// public sealed class NavMeshRuntime : INavMeshRuntime
// {
//     private readonly INavMeshService _navMesh;
//     private readonly HashSet<NavAgent> _agents = new();

//     public NavMeshRuntime(INavMeshService navMesh)
//     {
//         _navMesh = navMesh ?? throw new ArgumentNullException(nameof(navMesh));
//     }

//     public NavAgent CreateAgent(IPhysxBody3D body, float speed)
//     {
//         if (body is null)
//             throw new ArgumentNullException(nameof(body));

//         var snapped = _navMesh.SamplePosition(body.Position);
//         body.Position = snapped;

//         var agent = new NavAgent(body, speed);
//         _agents.Add(agent);
//         return agent;
//     }

//     public void RemoveAgent(NavAgent agent)
//     {
//         _agents.Remove(agent);
//     }

//     public bool SetDestination(NavAgent agent, Vector3 destination)
//     {
//         if (!_agents.Contains(agent))
//             throw new InvalidOperationException("Agent is not registered with this runtime.");

//         var start = agent.Position;
//         destination = _navMesh.SamplePosition(destination);

//         if (!_navMesh.TryFindPath(start, destination, out var path))
//         {
//             agent.CurrentPath = null;
//             agent.CurrentWaypointIndex = 0;
//             return false;
//         }

//         agent.CurrentPath = path;
//         agent.CurrentWaypointIndex = 0;
//         return true;
//     }

//     public void Update(float deltaTime)
//     {
//         if (deltaTime <= 0f)
//             return;

//         foreach (var agent in _agents)
//         {
//             if (!agent.HasPath)
//                 continue;

//             AdvanceAgent(agent, deltaTime);
//         }
//     }

//     private static void AdvanceAgent(NavAgent agent, float dt)
//     {
//         var path = agent.CurrentPath!;
//         var waypoints = path.Waypoints;
//         var idx = agent.CurrentWaypointIndex;

//         if (idx >= waypoints.Count)
//         {
//             agent.CurrentPath = null;
//             agent.Body.LinearVelocity = Vector3.Zero;
//             return;
//         }

//         var pos = agent.Position;
//         var target = waypoints[idx];

//         var toTarget = target - pos;
//         var distance = toTarget.Length();

//         // Close enough to this waypoint → move to next.
//         if (distance < 0.05f)
//         {
//             agent.CurrentWaypointIndex++;
//             if (agent.CurrentWaypointIndex >= waypoints.Count)
//             {
//                 agent.CurrentPath = null;
//                 agent.Body.LinearVelocity = Vector3.Zero;
//             }
//             return;
//         }

//         var dir = toTarget / MathF.Max(distance, 1e-6f);
//         var desiredVelocity = dir * agent.Speed;

//         // Physics will handle actual integration & collisions.
//         agent.Body.LinearVelocity = desiredVelocity;
//     }

// }
