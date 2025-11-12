// PhysxWorld2D.cs
using System.Numerics;

using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;

namespace Altruist.Physx
{

    public interface IPhysxWorldEngine2D : IDisposable
    {
        float FixedDeltaTime { get; }
        IReadOnlyCollection<IPhysxBody> Bodies { get; }
        void Step(float deltaTime);
        IPhysxBody2D AddBody(IPhysxBody2D body);
        void RemoveBody(IPhysxBody body);
        IEnumerable<PhysxRaycastHit2D> RayCast(PhysxRay2D ray, int maxHits = 1);
    }

    public interface IPhysxWorldEngineFactory2D
    {
        IPhysxWorldEngine2D Create(Vector2 gravity, float fixedDeltaTime = 1f / 60f);
    }

    [ConditionalOnConfig("altruist:game:engine:dimension", havingValue: "2D")]
    [Service(typeof(IPhysxBody2D))]
    [Service(typeof(IPhysxBody))]
    public sealed class PhysxWorld2D : IPhysxWorld2D, IDisposable
    {
        public IReadOnlyCollection<IPhysxBody> Bodies => _engine.Bodies;

        private readonly IPhysxWorldEngine2D _engine;

        public PhysxWorld2D(IPhysxWorldEngine2D engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        public void Step(float deltaTime) => _engine.Step(deltaTime);

        public void AddBody(IPhysxBody2D body) => _engine.AddBody(body);

        public void RemoveBody(IPhysxBody body) => _engine.RemoveBody(body);

        public IEnumerable<PhysxRaycastHit2D> RayCast(PhysxRay2D ray, int maxHits = 1)
            => _engine.RayCast(ray, maxHits);

        public void Dispose() => _engine.Dispose();
    }
}
