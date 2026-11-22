/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD.Navigation;

// `Path` deliberately lives in this sub-namespace so it doesn't collide
// with `System.IO.Path` (which is brought in transitively all over the
// codebase). Callers that want the facade opt in with:
//   using Altruist.Gaming.ThreeD.Navigation;
// and write Path.Detect(...). Everywhere else, just inject INavMeshService
// directly.

/// <summary>Sugar API around <see cref="INavMeshService"/>. Lets gameplay
/// code read like prose:
///
/// <code>
/// var path = Path.Detect(navMesh, zone, attacker, target);
/// if (path.IsComplete) ...
/// </code>
///
/// Internally just delegates — services injected via DI are still the
/// authoritative entry points for anything that needs to mock or override
/// the behavior in tests.</summary>
/// <remarks>Lives in <c>Altruist.Gaming.ThreeD.Navigation</c> to avoid clashing with <see cref="System.IO.Path"/>.</remarks>
public static class Path
{
    /// <summary>Find a path between two world positions in the given zone.
    /// Returns <see cref="NavPath.Empty"/> when no nav-mesh is registered
    /// for the zone or no path exists.</summary>
    /// <param name="service">Navmesh service to query.</param>
    /// <param name="zone">Navmesh zone key.</param>
    /// <param name="from">Start position.</param>
    /// <param name="to">Goal position.</param>
    /// <param name="snapDistance">Max XZ snap distance for both endpoints.</param>
    public static NavPath Detect(INavMeshService service, string zone, Vector3 from, Vector3 to, float snapDistance = 4f)
        => service.FindPath(zone, from, to, snapDistance);

    /// <summary>Find a path between two world objects (entities, props,
    /// anything with a <c>Transform.Position</c>) in the given zone.</summary>
    /// <param name="service">Navmesh service to query.</param>
    /// <param name="zone">Navmesh zone key.</param>
    /// <param name="from">Start object; <c>null</c> returns <see cref="NavPath.Empty"/>.</param>
    /// <param name="to">Goal object; <c>null</c> returns <see cref="NavPath.Empty"/>.</param>
    /// <param name="snapDistance">Max XZ snap distance for both endpoints.</param>
    public static NavPath Detect(INavMeshService service, string zone, IWorldObject3D from, IWorldObject3D to, float snapDistance = 4f)
    {
        if (from == null || to == null) return NavPath.Empty;
        var a = from.Transform.Position.ToVector3();
        var b = to.Transform.Position.ToVector3();
        return service.FindPath(zone, a, b, snapDistance);
    }
}
