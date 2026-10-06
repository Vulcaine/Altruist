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

        /// <summary>Creates a body in this world (already added: no <see cref="AddBody"/> needed).</summary>
        IPhysxBody2D CreateBody(in PhysxBodyDef2D def);

        /// <summary>Attaches a shape with its material and filter to a body of this world.</summary>
        IPhysxFixture2D CreateFixture(IPhysxBody2D body, in PhysxFixtureDef2D def);

        /// <summary>Receives this world's contact callbacks during <see cref="Step"/> (null removes it).</summary>
        void SetContactListener(IPhysxContactListener2D? listener);

        /// <summary>Casts a ray; the callback decides per hit whether to ignore, clip or stop.</summary>
        void RayCast(Vector2 from, Vector2 to, IPhysxRayCastCallback2D callback);

        /// <summary>
        /// The world's current contacts (touching or only overlapping bounding boxes; check
        /// <see cref="IPhysxContact2D.IsTouching"/>). Each item is valid until the next one.
        /// </summary>
        IEnumerable<IPhysxContact2D> Contacts { get; }
    }

    public interface IPhysxWorldEngineFactory2D
    {
        IPhysxWorldEngine2D Create(Vector2 gravity, float fixedDeltaTime = 1f / 60f);

        IPhysxWorldEngine2D Create(PhysxWorldSettings2D settings) => Create(settings.Gravity, settings.FixedDeltaTime);
    }

    /// <summary>
    /// Standalone 2D worlds outside the world organizer: match simulations, prediction or
    /// rollback scratch worlds. Each call is an independent, deterministic world.
    /// <para>
    /// Threading: one world is stepped by one thread at a time; different worlds may step on
    /// different threads at once (<see cref="ParallelWorldsSupported"/>, e.g. rooms stepped by
    /// the engine's <c>step-workers</c>), and a world may move between threads between steps.
    /// </para>
    /// </summary>
    public static class PhysxWorldEngine2D
    {
        public static IPhysxWorldEngine2D Create(PhysxWorldSettings2D settings) => new Box2DWorldEngine2D(settings);

        /// <summary>Independent worlds may be stepped on different threads at the same time.</summary>
        public static bool ParallelWorldsSupported => Box2DThreading.EnsureInstalled();
    }

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
