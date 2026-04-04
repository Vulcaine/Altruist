using System.Numerics;

using Altruist.Physx.Contracts;

using Box2DSharp.Dynamics;

namespace Altruist.Physx.TwoD
{
    [Service(typeof(IPhysxWorldEngineFactory2D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    public sealed class WorldEngineFactory2D : IPhysxWorldEngineFactory2D
    {
        public IPhysxWorldEngine2D Create(Vector2 gravity, float fixedDeltaTime = 1f / 60f)
            => new Box2DWorldEngine2D(gravity, fixedDeltaTime);
    }

    public sealed class Box2DWorldEngine2D : IPhysxWorldEngine2D
    {
        public float FixedDeltaTime { get; }
        public IReadOnlyCollection<IPhysxBody> Bodies => _bodies.Values.Cast<IPhysxBody>().ToList();

        internal World World => _world;

        public int Index { get; }
        private readonly World _world;
        private readonly Dictionary<string, Body2DAdapter> _bodies = new();
        private readonly Dictionary<Body, Body2DAdapter> _byNative = new();


        public Box2DWorldEngine2D(
            Vector2 gravity,
            float fixedDeltaTime = 1f / 60f
        )
        {
            FixedDeltaTime = fixedDeltaTime;
            _world = new World(gravity);
        }

        public void Step(float deltaTime)
        {
            if (_world.BodyCount == 0)
                return;
            _world.Step(deltaTime, velocityIterations: 8, positionIterations: 3);
        }

        public IPhysxBody2D AddBody(IPhysxBody2D body)
        {
            if (body is not Body2DAdapter adapter)
                throw new InvalidOperationException("This engine can only add bodies created by the Box2D provider.");

            _bodies[adapter.Id] = adapter;
            _byNative[adapter.Underlying] = adapter;
            return adapter;
        }

        public void RemoveBody(IPhysxBody body)
        {
            if (body is Body2DAdapter b && _bodies.Remove(b.Id))
            {
                _byNative.Remove(b.Underlying);
                _world.DestroyBody(b.Underlying);
            }
        }

        public IEnumerable<PhysxRaycastHit2D> RayCast(PhysxRay2D ray, int maxHits = 1)
        {
            var hits = new List<PhysxRaycastHit2D>(Math.Max(1, maxHits));
            var cb = new RayCastCollector(_byNative, hits, maxHits);
            _world.RayCast(cb, ray.From, ray.To);
            return hits;
        }

        public void Dispose()
        {
            foreach (var a in _bodies.Values.ToArray())
                _world.DestroyBody(a.Underlying);
            _bodies.Clear();
            _byNative.Clear();
        }

        private sealed class RayCastCollector : IRayCastCallback
        {
            private readonly Dictionary<Body, Body2DAdapter> _map;
            private readonly List<PhysxRaycastHit2D> _hits;
            private readonly int _max;

            public RayCastCollector(
                Dictionary<Body, Body2DAdapter> map,
                List<PhysxRaycastHit2D> hits,
                int max)
            {
                _map = map;
                _hits = hits;
                _max = max;
            }

            public float RayCastCallback(Fixture fixture, in Vector2 point, in Vector2 normal, float fraction)
            {
                if (_map.TryGetValue(fixture.Body, out var adapter))
                {
                    _hits.Add(new PhysxRaycastHit2D(adapter, point, normal, fraction));
                    if (_hits.Count >= _max)
                        return 0f;
                }
                return fraction;
            }
        }
    }
}
