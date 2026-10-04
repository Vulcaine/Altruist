using System.Numerics;
using System.Runtime.InteropServices;
using Altruist.Physx.Contracts;

namespace Altruist.Physx.ThreeD;

/// <summary>
/// Lightweight kinematic body used when the physics backend is disabled.
/// It gives higher-level systems such as KCC a body-shaped state carrier
/// without registering anything in a physics simulation.
/// </summary>
public sealed class InMemoryPhysxBody3D : IPhysxBody3D
{
    private readonly List<IPhysxCollider> _colliders = new();

    public InMemoryPhysxBody3D(in PhysxBody3DDesc desc)
    {
        Id = desc.Id;
        Type = desc.Type;
        Mass = desc.Mass;
        PhysxTag = desc.PhysxTag;
        Position = desc.Transform.Position.ToVector3();
        Rotation = desc.Transform.Rotation.ToQuaternion();
    }

    public string Id { get; }
    public PhysxBodyType Type { get; set; }
    public float Mass { get; set; }
    public PhysxTag? PhysxTag { get; set; }
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; }
    public Vector3 LinearVelocity { get; set; }
    public Vector3 AngularVelocity { get; set; }

    public void AddCollider(IPhysxCollider collider)
    {
        if (collider == null)
            throw new ArgumentNullException(nameof(collider));

        if (!_colliders.Contains(collider))
            _colliders.Add(collider);
    }

    public bool RemoveCollider(IPhysxCollider collider)
        => collider != null && _colliders.Remove(collider);

    public ReadOnlySpan<IPhysxCollider> GetColliders()
        => CollectionsMarshal.AsSpan(_colliders);

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

    public IPhysxCollider? GetColliderAt(int index)
        => (uint)index < (uint)_colliders.Count ? _colliders[index] : null;

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
