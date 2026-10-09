using System.Numerics;

using Altruist.Physx.Contracts;

namespace Altruist.Physx.ThreeD
{
    /// <summary>
    /// Backend-specific 3D simulation (e.g. <see cref="BepuWorldEngine3D"/>). Obtain one from
    /// <see cref="IPhysxWorldEngineFactory3D.Create"/>; wrap it in <see cref="PhysxWorld3D"/> for the
    /// engine-agnostic <see cref="IPhysxWorld3D"/> surface.
    /// </summary>
    /// <remarks>Units are world units and seconds; gravity is a per-second acceleration vector (typically −Y).</remarks>
    public interface IPhysxWorldEngine3D : IDisposable
    {
        /// <summary>Fixed simulation sub-step in seconds (default 1/60).</summary>
        float FixedDeltaTime { get; }
        /// <summary>Bodies registered with <see cref="AddBody"/> (a snapshot copy in the BEPU backend).</summary>
        IReadOnlyCollection<IPhysxBody3D> Bodies { get; }

        /// <summary>
        /// Advances the simulation by <paramref name="deltaTime"/> seconds using a fixed-step accumulator: runs as many
        /// <see cref="FixedDeltaTime"/> sub-steps as fit; the remainder carries over to the next call.
        /// </summary>
        /// <param name="deltaTime">Elapsed time in seconds.</param>
        void Step(float deltaTime);

        /// <summary>Removes a body from the simulation (the BEPU backend defers this to the start of the next <see cref="Step"/>).</summary>
        /// <param name="body">Body to remove; bodies from another backend are ignored.</param>
        void RemoveBody(IPhysxBody3D body);

        /// <summary>
        /// Registers a body created by <see cref="IPhysxBodyApiProvider3D.CreateBody"/> with this engine so it appears in
        /// <see cref="Bodies"/> and in query results.
        /// </summary>
        /// <param name="body">Body created for this engine.</param>
        void AddBody(IPhysxBody3D body);

        /// <summary>Casts a ray segment and returns hits sorted by distance.</summary>
        /// <param name="ray">Ray segment in world space.</param>
        /// <param name="maxHits">Maximum number of hits to return (nearest first); ≤ 0 returns none.</param>
        /// <param name="layerMask">
        /// Only bodies whose <see cref="PhysxTag.Layer"/> shares a bit with this mask are returned (untagged bodies count as
        /// <see cref="PhysxLayer.All"/>). See <see cref="PhysxLayer"/>.
        /// </param>
        /// <returns>Hits ordered by <see cref="PhysxRaycastHit3D.T"/>, possibly empty.</returns>
        IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1, uint layerMask = 0xFFFFFFFFu);

        /// <summary>
        /// Sweeps an upright (local Y axis) capsule from <paramref name="center"/> along <paramref name="direction"/> and
        /// returns the bodies it would hit, nearest first. Typical use: character-controller ground and wall probes.
        /// </summary>
        /// <param name="center">Capsule centre at the start of the sweep.</param>
        /// <param name="radius">Capsule radius.</param>
        /// <param name="halfLength">Half the distance between the two hemisphere centres.</param>
        /// <param name="direction">Sweep direction; normalised internally. Zero returns no hits.</param>
        /// <param name="maxDistance">Sweep distance in world units; must be positive and finite.</param>
        /// <param name="maxHits">Maximum number of hits to return.</param>
        /// <param name="layerMask">Layer filter, as in <see cref="RayCast"/>.</param>
        /// <returns>
        /// Hits ordered by distance. A shape already overlapped at the start is reported with <c>T = 0</c> and zero
        /// point/normal, and stops the sweep.
        /// </returns>
        IEnumerable<PhysxRaycastHit3D> CapsuleCast(
            Vector3 center,
            float radius,
            float halfLength,
            Vector3 direction,
            float maxDistance,
            int maxHits = 1,
            uint layerMask = 0xFFFFFFFFu);
    }

    /// <summary>Creates 3D world engines. Registered in DI by the active backend (BEPU: <see cref="BepuWorldEngineFactory3D"/>).</summary>
    public interface IPhysxWorldEngineFactory3D
    {
        /// <summary>
        /// Creates a new, independent engine. Each world needs its own engine: engines are never shared or reused, and the
        /// caller (typically through <see cref="PhysxWorld3D"/>) owns and disposes it.
        /// </summary>
        /// <param name="gravity">Gravity acceleration in world units per second² (e.g. <c>new Vector3(0, -9.81f, 0)</c>).</param>
        /// <param name="fixedDeltaTime">Fixed sub-step in seconds; non-positive, NaN or infinite values fall back to 1/60.</param>
        /// <example>
        /// <code>
        /// var engine = engineFactory.Create(new Vector3(0, -9.81f, 0));
        /// var world  = new PhysxWorld3D(engine);
        /// </code>
        /// </example>
        IPhysxWorldEngine3D Create(Vector3 gravity, float fixedDeltaTime = 1f / 60f);
    }

    /// <summary>Engine-agnostic 3D world facade (implemented by <see cref="PhysxWorld3D"/>) over an <see cref="IPhysxWorldEngine3D"/>.</summary>
    public interface IPhysxWorld3D : IPhysxWorld
    {
        /// <summary>The underlying backend engine; use it for queries the facade does not expose (e.g. <see cref="IPhysxWorldEngine3D.CapsuleCast"/>, layer-masked ray casts).</summary>
        IPhysxWorldEngine3D Engine { get; }
        /// <summary>Registers a body with the engine (see <see cref="IPhysxWorldEngine3D.AddBody"/>).</summary>
        /// <param name="body">Body created for this world's engine.</param>
        void AddBody(IPhysxBody3D body);
        /// <summary>Casts a ray against all layers (no layer mask; use <see cref="Engine"/> to filter by layer).</summary>
        /// <param name="ray">Ray segment in world space.</param>
        /// <param name="maxHits">Maximum hits to return, nearest first.</param>
        IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1);
    }
}

/// <summary>
/// Regular grid of terrain height samples on the XZ plane (Y up), produced by the heightmap loaders
/// (e.g. <c>IHeightmapLoader.PNG</c>) and consumed by heightfield colliders. Sample <c>(x, z)</c> lies at local
/// position <c>(x * CellSizeX, Heights[x, z] * HeightScale, z * CellSizeZ)</c>.
/// </summary>
public sealed class HeightfieldData
{
    /// <summary>Number of samples along X.</summary>
    public int Width { get; init; }
    /// <summary>Number of samples along Z (named Height after the image dimension, not the vertical axis).</summary>
    public int Height { get; init; }

    /// <summary>World-space distance between samples along X.</summary>
    public float CellSizeX { get; init; }

    /// <summary>World-space distance between samples along Z.</summary>
    public float CellSizeZ { get; init; }

    /// <summary>Multiplier from stored height to world-space Y.</summary>
    public float HeightScale { get; init; }

    /// <summary>Heights[x, z] in whatever units you decided (usually 0..1 before HeightScale).</summary>
    public float[,] Heights { get; init; } = default!;
}
