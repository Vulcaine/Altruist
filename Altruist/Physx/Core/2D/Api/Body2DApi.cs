// BodyApi.cs
using System.Numerics;
using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD.Numerics;

namespace Altruist.Physx.TwoD
{
    public interface IPhysxBodyApiProvider2D
    {
        IPhysxBody2D CreateBody(PhysxBodyType type, float mass, Transform2D transform);
    }

    public static class PhysxBody2D
    {
        public static IPhysxBodyApiProvider2D Provider { get; set; } = default!;
        public static IPhysxBody2D Create(float mass, Transform2D transform) =>
            Provider.CreateBody(mass > 0 ? PhysxBodyType.Dynamic : PhysxBodyType.Static, mass, transform);
        public static IPhysxBody2D Create(PhysxBodyType type, float mass, Transform2D transform) =>
            Provider.CreateBody(type, mass, transform);
    }
}
