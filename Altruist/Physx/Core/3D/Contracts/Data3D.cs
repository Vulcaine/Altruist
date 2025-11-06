using System.Numerics;
using Altruist.Physx.Contracts;

namespace Altruist.Physx.ThreeD
{
    public interface IPhysxBody3D : IPhysxBody
    {
        Vector3 Position { get; set; }
        Quaternion Rotation { get; set; }
        Vector3 LinearVelocity { get; set; }
        Vector3 AngularVelocity { get; set; }
    }

}