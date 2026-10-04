using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;

namespace Tests.Altruist.Physx.Fakes;

/// <summary>Bare-minimum <see cref="IPhysxBody2D"/> for tests that need a
/// settable Position / RotationZ / LinearVelocity but no real physics.
/// Mirror of <see cref="FakeBody"/>.</summary>
public class FakeBody2D : IPhysxBody2D
{
    public Vector2 Position { get; set; }
    public virtual Vector2 LinearVelocity { get; set; }
    public float RotationZ { get; set; }
    public float AngularVelocityZ { get; set; }
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public PhysxBodyType Type { get; set; } = PhysxBodyType.Kinematic;
    public float Mass { get; set; } = 1f;
    public PhysxTag? PhysxTag { get; set; }

    public PhysxForce? LastForce { get; private set; }
    public int ForceCallCount { get; private set; }

    public void AddCollider(IPhysxCollider collider) { }
    public bool RemoveCollider(IPhysxCollider collider) => false;
    public ReadOnlySpan<IPhysxCollider> GetColliders() => ReadOnlySpan<IPhysxCollider>.Empty;
    public void ApplyForce(in PhysxForce force)
    {
        LastForce = force;
        ForceCallCount++;
    }
    public bool TryGetColliderById(string colliderId, out IPhysxCollider collider) { collider = null!; return false; }
    public IPhysxCollider? GetColliderAt(int index) => null;
}
