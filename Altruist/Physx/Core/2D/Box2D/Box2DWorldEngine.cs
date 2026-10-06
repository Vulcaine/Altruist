using System.Numerics;

using Altruist.Physx.Contracts;

using Box2DSharp.Collision.Collider;
using Box2DSharp.Dynamics;
using Box2DSharp.Dynamics.Contacts;

namespace Altruist.Physx.TwoD
{
    [Service(typeof(IPhysxWorldEngineFactory2D))]
    [ConditionalOnConfig("altruist:environment:mode", havingValue: "2D")]
    public sealed class WorldEngineFactory2D : IPhysxWorldEngineFactory2D
    {
        public IPhysxWorldEngine2D Create(Vector2 gravity, float fixedDeltaTime = 1f / 60f)
            => new Box2DWorldEngine2D(gravity, fixedDeltaTime);

        public IPhysxWorldEngine2D Create(PhysxWorldSettings2D settings) => new Box2DWorldEngine2D(settings);
    }

    public sealed class Box2DWorldEngine2D : IPhysxWorldEngine2D
    {
        public float FixedDeltaTime { get; }
        public PhysxWorldSettings2D Settings { get; }
        public IReadOnlyCollection<IPhysxBody> Bodies => _bodies.Values.Cast<IPhysxBody>().ToList();

        internal World World => _world;

        public int Index { get; }
        private readonly World _world;
        private readonly Dictionary<string, Body2DAdapter> _bodies = new();
        private readonly Dictionary<Body, Body2DAdapter> _byNative = new();
        private readonly ContactBridge _contacts;
        private readonly RayCastBridge _rayCast = new();
        private readonly Box2DContact2D _enumerated = new();

        public Box2DWorldEngine2D(
            Vector2 gravity,
            float fixedDeltaTime = 1f / 60f
        ) : this(new PhysxWorldSettings2D { Gravity = gravity, FixedDeltaTime = fixedDeltaTime })
        {
        }

        static Box2DWorldEngine2D() => Box2DThreading.EnsureInstalled();

        public Box2DWorldEngine2D(PhysxWorldSettings2D settings)
        {
            Settings = settings;
            FixedDeltaTime = settings.FixedDeltaTime;
            _world = new World(settings.Gravity);
            _contacts = new ContactBridge();
            _world.SetContactListener(_contacts);
        }

        public void Step(float deltaTime)
        {
            if (_world.BodyCount == 0)
                return;
            _world.Step(deltaTime, Settings.VelocityIterations, Settings.PositionIterations);
        }

        public IPhysxBody2D AddBody(IPhysxBody2D body)
        {
            if (body is not Body2DAdapter adapter)
                throw new InvalidOperationException("This engine can only add bodies created by the Box2D provider.");

            _bodies[adapter.Id] = adapter;
            _byNative[adapter.Underlying] = adapter;
            return adapter;
        }

        public IPhysxBody2D CreateBody(in PhysxBodyDef2D def)
        {
            var body = _world.CreateBody(new BodyDef
            {
                BodyType = def.Type switch
                {
                    PhysxBodyType.Dynamic => BodyType.DynamicBody,
                    PhysxBodyType.Kinematic => BodyType.KinematicBody,
                    _ => BodyType.StaticBody,
                },
                Position = def.Position,
                Angle = def.Angle,
                Bullet = def.Bullet,
                AllowSleep = def.AllowSleep,
                FixedRotation = def.FixedRotation,
                LinearDamping = def.LinearDamping,
                AngularDamping = def.AngularDamping,
                GravityScale = def.GravityScale,
            });
            var adapter = new Body2DAdapter(def.Id ?? Guid.NewGuid().ToString("N"), body, def.Type) { UserData = def.UserData };
            return AddBody(adapter);
        }

        public IPhysxFixture2D CreateFixture(IPhysxBody2D body, in PhysxFixtureDef2D def)
        {
            if (body is not Body2DAdapter owner)
                throw new InvalidOperationException("Body must be created by this engine.");
            var fixture = new Box2DFixture2D(owner, def);
            fixture.Native = owner.Underlying.CreateFixture(new FixtureDef
            {
                Shape = Box2DShapes.Create(def.Shape),
                Density = def.Density,
                Friction = def.Friction,
                Restitution = def.Restitution,
                IsSensor = def.IsTrigger,
                Filter = new Filter { CategoryBits = def.Filter.Category, MaskBits = def.Filter.Mask, GroupIndex = def.Filter.Group },
                UserData = fixture,
            });
            owner.AddCollider(fixture);
            return fixture;
        }

        public void RemoveBody(IPhysxBody body)
        {
            if (body is Body2DAdapter b && _bodies.Remove(b.Id))
            {
                _byNative.Remove(b.Underlying);
                _world.DestroyBody(b.Underlying);
            }
        }

        public void SetContactListener(IPhysxContactListener2D? listener) => _contacts.Listener = listener;

        public IEnumerable<PhysxRaycastHit2D> RayCast(PhysxRay2D ray, int maxHits = 1)
        {
            var hits = new List<PhysxRaycastHit2D>(Math.Max(1, maxHits));
            var cb = new RayCastCollector(_byNative, hits, maxHits);
            _world.RayCast(cb, ray.From, ray.To);
            return hits;
        }

        public void RayCast(Vector2 from, Vector2 to, IPhysxRayCastCallback2D callback)
        {
            // Reused bridge; a nested ray cast from inside a callback gets its own.
            var bridge = _rayCast.Callback is null ? _rayCast : new RayCastBridge();
            bridge.Callback = callback;
            try
            {
                IRayCastCallback cb = bridge;
                _world.RayCast(cb, from, to);
            }
            finally
            {
                bridge.Callback = null;
            }
        }

        public IEnumerable<IPhysxContact2D> Contacts
        {
            get
            {
                foreach (var c in _world.ContactManager.ContactList)
                {
                    _enumerated.Native = c;
                    yield return _enumerated;
                }
                _enumerated.Native = null;
            }
        }

        public void Dispose()
        {
            foreach (var a in _bodies.Values.ToArray())
                _world.DestroyBody(a.Underlying);
            _bodies.Clear();
            _byNative.Clear();
        }

        /// <summary>Forwards Box2D contact callbacks to the game's listener and the fixtures' events.</summary>
        private sealed class ContactBridge : IContactListener
        {
            public IPhysxContactListener2D? Listener;
            private readonly Box2DContact2D _contact = new();

            public void BeginContact(Contact contact)
            {
                _contact.Native = contact;
                Listener?.BeginContact(_contact);
                Box2DFixture2D.RaiseBegin(_contact);
                _contact.Native = null;
            }

            public void EndContact(Contact contact)
            {
                _contact.Native = contact;
                Listener?.EndContact(_contact);
                Box2DFixture2D.RaiseEnd(_contact);
                _contact.Native = null;
            }

            public void PreSolve(Contact contact, in Manifold oldManifold)
            {
                if (Listener is null) return;
                _contact.Native = contact;
                Listener.PreSolve(_contact);
                _contact.Native = null;
            }

            public void PostSolve(Contact contact, in ContactImpulse impulse)
            {
                if (Listener is null) return;
                _contact.Native = contact;
                var max = 0f;
                for (var i = 0; i < impulse.Count; i++) max = MathF.Max(max, impulse.NormalImpulses[i]);
                Listener.PostSolve(_contact, max);
                _contact.Native = null;
            }
        }

        private sealed class RayCastBridge : IRayCastCallback
        {
            public IPhysxRayCastCallback2D? Callback;

            public float RayCastCallback(Fixture fixture, in Vector2 point, in Vector2 normal, float fraction)
                => Callback!.OnHit(Box2DFixture2D.Of(fixture), point, normal, fraction);
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
