
using System.Numerics;

using Altruist.Physx.Contracts;

namespace Altruist.Physx.ThreeD
{
    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "3D")]
    [Service(typeof(IPhysxBody3D))]
    [Service(typeof(IPhysxBody))]

    public sealed class PhysxWorld3D : IPhysxWorld3D, IDisposable
    {
        public IReadOnlyCollection<IPhysxBody> Bodies => _engine.Bodies;

        private readonly IPhysxWorldEngine3D _engine;

        public PhysxWorld3D(IPhysxWorldEngine3D engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public void Step(float deltaTime) => _engine.Step(deltaTime);

        public void AddBody(IPhysxBody3D body) => _engine.AddBody(body);

        public void RemoveBody(IPhysxBody3D body) => _engine.RemoveBody(body);

        public IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1)
            => _engine.RayCast(ray, maxHits);

        public void Dispose() => _engine.Dispose();

        public static class Contracts
        {
            public interface IPhysxWorldEngine3D : IDisposable
            {
                float FixedDeltaTime { get; }
                IReadOnlyCollection<IPhysxBody> Bodies { get; }
                void Step(float deltaTime);
                IPhysxBody3D AddBody(IPhysxBody3D body);
                void RemoveBody(IPhysxBody body);
                IEnumerable<PhysxRaycastHit3D> RayCast(PhysxRay3D ray, int maxHits = 1);
            }

            public interface IPhysxWorldEngineFactory3D
            {
                IPhysxWorldEngine3D Create(Vector3 gravity, float fixedDeltaTime = 1f / 60f);
            }
        }
    }
}
