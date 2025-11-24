using System.Numerics;

using Altruist.Physx.Contracts;

namespace Altruist.Physx.ThreeD
{
    public interface IPhysxWorldEngine3D : IDisposable
    {
        float FixedDeltaTime { get; }
        IReadOnlyCollection<IPhysxBody3D> Bodies { get; }

        void Step(float deltaTime);

        void RemoveBody(IPhysxBody3D body);

        void AddBody(IPhysxBody3D body);

        IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1);
    }

    public interface IPhysxWorldEngineFactory3D
    {
        IPhysxWorldEngine3D Create(Vector3 gravity, float fixedDeltaTime = 1f / 60f);
    }

    public interface IPhysxWorld3D : IPhysxWorld
    {
        IPhysxWorldEngine3D Engine { get; }
        void AddBody(IPhysxBody3D body);
        IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1);
    }
}
