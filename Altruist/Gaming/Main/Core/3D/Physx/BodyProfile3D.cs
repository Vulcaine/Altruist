using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD;
using Altruist.ThreeD.Numerics;

namespace Altruist.Gaming.ThreeD;

public interface IBodyProfile3D
{
    PhysxBody3DDesc CreateBody(Transform3D transform);
}


public sealed class HumanoidCapsuleBodyProfile : IBodyProfile3D
{
    public float Radius { get; }
    public float HalfLength { get; }
    public float Mass { get; }

    public HumanoidCapsuleBodyProfile(float radius, float halfLength, float mass)
    {
        Radius = radius;
        HalfLength = halfLength;
        Mass = mass;
    }

    public PhysxBody3DDesc CreateBody(Transform3D transform)
    {
        var sized = transform.WithSize(Size3D.Of(Radius, HalfLength, 0f));
        return PhysxBody3D.Create(PhysxBodyType.Dynamic, Mass, sized);
    }
}
