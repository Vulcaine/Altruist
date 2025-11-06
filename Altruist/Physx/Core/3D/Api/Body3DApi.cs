
using Altruist.Physx.Contracts;
using Altruist.Physx.ThreeD.Numerics;

namespace Altruist.Physx.ThreeD
{
    public interface IPhysxBodyApiProvider3D
    {
        IPhysxBody3D CreateBody(PhysxBodyType type, float mass, Transform3D transform);
    }

    public static class PhysxBody3D
    {
        public static IPhysxBodyApiProvider3D Provider { get; set; } = default!;

        public static IPhysxBody3D Create(float mass, Size3D size, Position3D position)
            => Provider.CreateBody(
                mass > 0 ? PhysxBodyType.Dynamic : PhysxBodyType.Static,
                mass,
                new Transform3D(position, size, Scale3D.One, Rotation3D.Identity));

        public static IPhysxBody3D Create(PhysxBodyType type, float mass, Transform3D transform)
            => Provider.CreateBody(type, mass, transform);
    }
}
