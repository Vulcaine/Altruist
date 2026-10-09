using System.Numerics;
using System.Runtime.InteropServices;
using Altruist.Physx.Contracts;

namespace Altruist.Physx.ThreeD;

/// <summary>
/// Lightweight kinematic body used when the physics backend is disabled.
/// It gives higher-level systems such as KCC a body-shaped state carrier
/// without registering anything in a physics simulation.
/// </summary>
/// <remarks>
/// Plain auto-properties: nothing integrates velocity or gravity, so the owner must move <see cref="Position"/> itself.
/// <see cref="ApplyForce"/> only honours the set-velocity commands; forces, impulses and torques are ignored.
/// Not thread-safe.
/// </remarks>
public sealed class InMemoryPhysxBody3D : IPhysxBody3D
{
    private readonly List<IPhysxCollider> _colliders = new();

    /// <summary>Creates the body from a descriptor (id, type, mass, tag, initial position and rotation).</summary>
    /// <param name="desc">Body descriptor; its size is ignored.</param>
    public InMemoryPhysxBody3D(in PhysxBody3DDesc desc)
    {
        Id = desc.Id;
        Type = desc.Type;
        Mass = desc.Mass;
        PhysxTag = desc.PhysxTag;
        Position = desc.Transform.Position.ToVector3();
        Rotation = desc.Transform.Rotation.ToQuaternion();
    }

    /// <inheritdoc/>
    public string Id { get; }
    /// <inheritdoc/>
    public PhysxBodyType Type { get; set; }
    /// <inheritdoc/>
    public float Mass { get; set; }
    /// <inheritdoc/>
    public PhysxTag? PhysxTag { get; set; }
    /// <inheritdoc/>
    public Vector3 Position { get; set; }
    /// <inheritdoc/>
    public Quaternion Rotation { get; set; }
    /// <inheritdoc/>
    public Vector3 LinearVelocity { get; set; }
    /// <inheritdoc/>
    public Vector3 AngularVelocity { get; set; }

    /// <inheritdoc/>
    public void AddCollider(IPhysxCollider collider)
    {
        if (collider == null)
            throw new ArgumentNullException(nameof(collider));

        if (!_colliders.Contains(collider))
            _colliders.Add(collider);
    }

    /// <inheritdoc/>
    public bool RemoveCollider(IPhysxCollider collider)
        => collider != null && _colliders.Remove(collider);

    /// <inheritdoc/>
    public ReadOnlySpan<IPhysxCollider> GetColliders()
        => CollectionsMarshal.AsSpan(_colliders);

    /// <inheritdoc/>
    public bool TryGetColliderById(string colliderId, out IPhysxCollider collider)
    {
        if (!string.IsNullOrEmpty(colliderId))
        {
            foreach (var item in _colliders)
            {
                if (string.Equals(item.Id, colliderId, StringComparison.Ordinal))
                {
                    collider = item;
                    return true;
                }
            }
        }

        collider = default!;
        return false;
    }

    /// <inheritdoc/>
    public IPhysxCollider? GetColliderAt(int index)
        => (uint)index < (uint)_colliders.Count ? _colliders[index] : null;

    /// <summary>
    /// Applies only <see cref="PhysxForce.Kind.SetLinearVelocity3D"/> and <see cref="PhysxForce.Kind.SetAngularVelocity3D"/>;
    /// every other kind is silently ignored.
    /// </summary>
    /// <param name="force">Force command.</param>
    public void ApplyForce(in PhysxForce force)
    {
        switch (force.Type)
        {
            case PhysxForce.Kind.SetLinearVelocity3D:
                LinearVelocity = force.Vector;
                break;
            case PhysxForce.Kind.SetAngularVelocity3D:
                AngularVelocity = force.Vector;
                break;
        }
    }
}
