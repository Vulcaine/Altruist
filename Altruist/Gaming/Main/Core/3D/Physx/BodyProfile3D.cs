using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD;

/// <summary>
/// Reusable recipe that turns a spawn transform into a physics body descriptor plus its collider descriptors, so every
/// entity of one archetype (e.g. a player character) is built with the same shape, mass and layer.
/// </summary>
/// <remarks>
/// Use it when several call sites spawn the same kind of body; for one-off bodies call
/// <see cref="PhysxBody3D.Create(PhysxBodyType, float, Transform3D, bool, System.Nullable{PhysxTag})"/> and <see cref="PhysxCollider3D"/>
/// directly. The 2D counterpart is <see cref="Altruist.Gaming.TwoD.IBodyProfile2D"/>. Implementations only build
/// descriptors; adding them to a world is the caller's job.
/// </remarks>
public interface IBodyProfile3D
{
    /// <summary>Builds the body descriptor for an entity spawned at <paramref name="transform"/>.</summary>
    /// <param name="transform">Spawn transform (position/rotation); implementations may replace its size.</param>
    /// <returns>A body descriptor ready to be added to a physics world.</returns>
    PhysxBody3DDesc CreateBody(Transform3D transform);
    /// <summary>Builds the collider descriptors to attach to the body created by <see cref="CreateBody"/>.</summary>
    /// <param name="transform">Same spawn transform passed to <see cref="CreateBody"/>.</param>
    /// <returns>One or more collider descriptors (lazily enumerated).</returns>
    IEnumerable<PhysxCollider3DDesc> CreateColliders(Transform3D transform);
}

/// <summary>
/// <see cref="IBodyProfile3D"/> for an upright (local +Y) capsule character: a dynamic body tagged
/// <see cref="PhysxLayer.Character"/> with one solid capsule collider.
/// </summary>
/// <remarks>
/// Pair it with <see cref="KinematicCharacterController3D"/> (pass <c>isKinematic: true</c> so the solver does not move the
/// body) for player-style movement; set its <see cref="KinematicCharacterController3D.Radius"/>/<see cref="KinematicCharacterController3D.Height"/>
/// to match (<c>Height = 2 * (halfLength + radius)</c>). The capsule size is stored as <c>(radius, halfLength, 0)</c>,
/// matching <see cref="PhysxCollider3D.CreateCapsule"/>.
/// </remarks>
/// <example><code>
/// var profile = new HumanoidCapsuleBodyProfile(radius: 0.3f, halfLength: 0.6f, mass: 75f, isKinematic: true);
/// var bodyDesc = profile.CreateBody(spawnTransform);
/// var colliders = profile.CreateColliders(spawnTransform);
/// </code></example>
public sealed class HumanoidCapsuleBodyProfile : IBodyProfile3D
{
    /// <summary>Capsule radius in world units.</summary>
    public float Radius { get; }
    /// <summary>Half the length of the inner segment between the two hemisphere centres, in world units.</summary>
    public float HalfLength { get; }
    /// <summary>Body mass passed to the physics backend.</summary>
    public float Mass { get; }

    /// <summary>When true the body is created kinematic (moved by code, e.g. a character controller, not by the solver).</summary>
    public bool IsKinematic { get; }

    /// <summary>Creates a capsule character profile.</summary>
    /// <param name="radius">Capsule radius in world units.</param>
    /// <param name="halfLength">Half the inner segment length; total height is <c>2 * (halfLength + radius)</c>.</param>
    /// <param name="mass">Body mass.</param>
    /// <param name="isKinematic">Create the body kinematic instead of solver-driven.</param>
    public HumanoidCapsuleBodyProfile(float radius, float halfLength, float mass, bool isKinematic = false)
    {
        Radius = radius;
        HalfLength = halfLength;
        Mass = mass;
        IsKinematic = isKinematic;
    }

    /// <inheritdoc/>
    /// <remarks>Always <see cref="PhysxBodyType.Dynamic"/> with <see cref="IsKinematic"/> applied, tagged <see cref="PhysxLayer.Character"/>.</remarks>
    public PhysxBody3DDesc CreateBody(Transform3D transform)
    {
        var sized = transform.WithSize(Size3D.Of(Radius, HalfLength, 0f));
        return PhysxBody3D.Create(PhysxBodyType.Dynamic, Mass, sized, isKinematic: IsKinematic, physxTag: new PhysxTag((uint)PhysxLayer.Character));
    }

    /// <inheritdoc/>
    /// <remarks>Yields a single non-trigger <see cref="PhysxColliderShape3D.Capsule3D"/>.</remarks>
    public IEnumerable<PhysxCollider3DDesc> CreateColliders(Transform3D transform)
    {
        var sized = transform.WithSize(Size3D.Of(Radius, HalfLength, 0f));
        yield return PhysxCollider3D.Create(PhysxColliderShape3D.Capsule3D, sized, isTrigger: false);
    }
}

