using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;

namespace Tests.Altruist.Physx.Fakes;

/// <summary>Bare-minimum <see cref="IPhysxBody3D"/> for tests that need a
/// settable Position / Rotation / LinearVelocity but no real physics.
/// Tracks <see cref="ApplyForce"/> calls in <see cref="LastForce"/> for
/// assertions on launch/impulse extensions.</summary>
public class FakeBody : IPhysxBody3D
{
    public Vector3 Position { get; set; }
    public Quaternion Rotation { get; set; } = Quaternion.Identity;
    public virtual Vector3 LinearVelocity { get; set; }
    public Vector3 AngularVelocity { get; set; }
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

public sealed class ThrowingBody : FakeBody
{
    public override Vector3 LinearVelocity
    {
        get => Vector3.Zero;
        set => throw new InvalidOperationException("simulated body fault");
    }
}
