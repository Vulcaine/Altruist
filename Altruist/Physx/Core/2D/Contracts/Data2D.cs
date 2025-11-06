using System.Numerics;
using Altruist.Physx.Contracts;

namespace Altruist.Physx.TwoD
{

    public interface IPhysxBody2D : IPhysxBody
    {
        Vector2 Position { get; set; }
        float RotationZ { get; set; }
        Vector2 LinearVelocity { get; set; }
        float AngularVelocityZ { get; set; }
    }


    public interface IPhysxWorld2D : IPhysxWorld
    {
        IEnumerable<PhysxRaycastHit2D> RayCast(PhysxRay2D ray, int maxHits = 1);
    }


}