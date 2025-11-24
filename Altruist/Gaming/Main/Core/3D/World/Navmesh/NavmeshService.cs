// using System.Numerics;

// namespace Altruist.Gaming.ThreeD;

// public interface INavMeshService
// {
//     /// <summary>
//     /// Attempt to find a path on the navmesh from start to end.
//     /// Returns false if no path exists.
//     /// </summary>
//     bool TryFindPath(Vector3 start, Vector3 end, out NavPath path);

//     /// <summary>
//     /// Clamp/slide a point to the nearest position on the navmesh.
//     /// </summary>
//     Vector3 SamplePosition(Vector3 point, float maxDistance = 2f);
// }

// [Service(typeof(INavMeshService))]
// public sealed class NavMeshService : INavMeshService
// {
//     private readonly NavMeshSchema _data;

//     // You inject NavMeshData from your WorldLoader/NavMeshLoader
//     public NavMeshService(NavMeshSchema data)
//     {
//         _data = data ?? throw new ArgumentNullException(nameof(data));

//         // Later: build adjacency graph, spatial accel structures, etc.
//         // BuildGraph(_data);
//     }

//     public bool TryFindPath(Vector3 start, Vector3 end, out NavPath path)
//     {
//         // TODO: real pathfinding.
//         // For now stub: direct "path" with 2 points, if both are on mesh.

//         var from = SamplePosition(start);
//         var to = SamplePosition(end);

//         // If either sample failed in a real impl, you'd return false.
//         var waypoints = new List<Vector3> { from, to };
//         path = new NavPath(waypoints, isComplete: true);
//         return true;
//     }

//     public Vector3 SamplePosition(Vector3 point, float maxDistance = 2f)
//     {
//         // TODO: nearest point search on navmesh polygons

//         // Stub: return the incoming point (you'll replace this)
//         return point;
//     }
// }
