using System.Numerics;

using Altruist.Physx.Contracts;

using Box2DSharp.Collision.Collider;
using Box2DSharp.Dynamics;
using Box2DSharp.Dynamics.Contacts;

namespace Altruist.Physx.TwoD
{
    /// <summary>
    /// Creates Box2D worlds. Registered in every environment mode (it holds no state and creates
    /// nothing until asked), so a 3D server can still run standalone 2D simulations.
    /// </summary>
    [Service(typeof(IPhysxWorldEngineFactory2D))]
    public sealed class WorldEngineFactory2D : IPhysxWorldEngineFactory2D
    {
        public IPhysxWorldEngine2D Create(Vector2 gravity, float fixedDeltaTime = 1f / 60f)
            => new Box2DWorldEngine2D(gravity, fixedDeltaTime);

        public IPhysxWorldEngine2D Create(PhysxWorldSettings2D settings) => new Box2DWorldEngine2D(settings);
    }

    public sealed class Box2DWorldEngine2D : IPhysxWorldEngine2D
    {
        public float FixedDeltaTime { get; private set; }
        public PhysxWorldSettings2D Settings { get; private set; }
        public IReadOnlyCollection<IPhysxBody> Bodies => _bodies.Values.Cast<IPhysxBody>().ToList();

        internal World World => _world;

        public int Index { get; }
        private readonly World _world;
        private readonly Dictionary<string, Body2DAdapter> _bodies = new();
        private readonly Dictionary<Body, Body2DAdapter> _byNative = new();
        private readonly ContactBridge _contacts;
        private readonly RayCastBridge _rayCast = new();
        private float _accumulator;

        // Bumped whenever contacts may be destroyed (and their pooled natives reused), so a
        // contact view handed out by Contacts before that throws instead of reading another contact.
        internal int ContactGeneration;

        // Fixtures of this world with OnCollisionStay subscribers; the contact walk after a step
        // only runs while there are some.
        internal int StaySubscribers;

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

        public void ApplySettings(PhysxWorldSettings2D settings)
        {
            Settings = settings;
            FixedDeltaTime = settings.FixedDeltaTime;
            _world.Gravity = settings.Gravity;
            _accumulator = 0f;
        }

        public void Step(float deltaTime)
        {
            if (_world.BodyCount == 0)
                return;

            var fixedDt = Settings.FixedDeltaTime;
            var maxSubSteps = Settings.MaxSubSteps;
            if (maxSubSteps <= 0 || fixedDt <= 0f)
            {
                StepOnce(deltaTime);
                return;
            }

            // Fixed stepping (opt-in): whole steps of the fixed dt, bounded per call.
            _accumulator += deltaTime;
            var steps = 0;
            while (_accumulator >= fixedDt && steps < maxSubSteps)
            {
                StepOnce(fixedDt);
                _accumulator -= fixedDt;
                steps++;
            }
            if (_accumulator >= fixedDt)
                _accumulator %= fixedDt;
        }

        private void StepOnce(float dt)
        {
            ContactGeneration++;
            _world.Step(dt, Settings.VelocityIterations, Settings.PositionIterations);
            _contacts.AfterStep(_world, StaySubscribers > 0);
        }

        public IPhysxBody2D AddBody(IPhysxBody2D body)
        {
            if (body is not Body2DAdapter adapter)
                throw new InvalidOperationException("This engine can only add bodies created by the Box2D provider.");

            _bodies[adapter.Id] = adapter;
            _byNative[adapter.Underlying] = adapter;
            adapter.Engine = this;
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
                ContactGeneration++;
                _world.DestroyBody(b.Underlying);
            }
        }

        public void SetContactListener(IPhysxContactListener2D? listener) => _contacts.Listener = listener;

        /// <summary>
        /// The closest <paramref name="maxHits"/> (at least 1) bodies of this world along the ray,
        /// sorted by fraction. Box2D reports hits in no particular order, so every hit is collected
        /// (a single hit clips the ray instead), then sorted and trimmed.
        /// </summary>
        public IEnumerable<PhysxRaycastHit2D> RayCast(PhysxRay2D ray, int maxHits = 1)
        {
            maxHits = Math.Max(1, maxHits);
            var hits = new List<PhysxRaycastHit2D>(maxHits);
            var cb = new RayCastCollector(_byNative, hits, clip: maxHits == 1);
            _world.RayCast(cb, ray.From, ray.To);
            if (hits.Count > 1)
            {
                // Stable (equal fractions keep Box2D's order), so identical worlds give identical results.
                hits = hits.OrderBy(h => h.Fraction).ToList();
                if (hits.Count > maxHits)
                    hits.RemoveRange(maxHits, hits.Count - maxHits);
            }
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

        /// <summary>
        /// The world's current contacts, each a distinct view over its native contact (collecting
        /// them is safe). A view stays valid until the contact list may change (the next step,
        /// removing or disabling a body, destroying a fixture); <see cref="IPhysxContact2D.ToInfo"/>
        /// copies one to keep it longer.
        /// </summary>
        public IEnumerable<IPhysxContact2D> Contacts
        {
            get
            {
                foreach (var c in _world.ContactManager.ContactList)
                    yield return new Box2DContact2D(c, this);
            }
        }

        public void Dispose()
        {
            ContactGeneration++;
            foreach (var a in _bodies.Values.ToArray())
                _world.DestroyBody(a.Underlying);
            _bodies.Clear();
            _byNative.Clear();
        }

        /// <summary>
        /// Forwards Box2D contact callbacks to the game's listener and the fixtures' events. One
        /// contact view is reused for every callback, so the per-step path does not allocate.
        /// </summary>
        private sealed class ContactBridge : IContactListener
        {
            public IPhysxContactListener2D? Listener;
            private readonly Box2DContact2D _contact = new();

            // Collisions that began this step: their enter events are raised after the step, so
            // they carry the normal impulse the solvers applied in it (summed from post-solve: the
            // regular solve and, for a landing caught by continuous collision, the TOI solve).
            private readonly List<Contact> _entered = new();
            private readonly List<float> _enteredImpulse = new();

            public void BeginContact(Contact contact)
            {
                _contact.Native = contact;
                Listener?.BeginContact(_contact);
                if (Box2DFixture2D.RaiseBegin(_contact))
                {
                    _entered.Add(contact);
                    _enteredImpulse.Add(0f);
                }
                _contact.Native = null;
            }

            public void EndContact(Contact contact)
            {
                _contact.Native = contact;
                Listener?.EndContact(_contact);
                // Ended before its enter was raised (same step, or a body removed): enter first.
                var pending = _entered.IndexOf(contact);
                if (pending >= 0)
                {
                    var impulse = _enteredImpulse[pending];
                    _entered.RemoveAt(pending);
                    _enteredImpulse.RemoveAt(pending);
                    Box2DFixture2D.RaiseCollisionEnter(_contact, impulse);
                }
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
                if (_entered.Count > 0)
                {
                    var pending = _entered.IndexOf(contact);
                    if (pending >= 0)
                    {
                        var sum = 0f;
                        for (var i = 0; i < impulse.Count; i++) sum += impulse.NormalImpulses[i];
                        _enteredImpulse[pending] += sum;
                    }
                }
                if (Listener is null) return;
                _contact.Native = contact;
                var max = 0f;
                for (var i = 0; i < impulse.Count; i++) max = MathF.Max(max, impulse.NormalImpulses[i]);
                Listener.PostSolve(_contact, max);
                _contact.Native = null;
            }

            /// <summary>
            /// After each world step: the step's collision enters, then a stay for every other
            /// touching, enabled, non-sensor contact (when some fixture listens for stays).
            /// </summary>
            public void AfterStep(World world, bool stay)
            {
                if (_entered.Count == 0 && !stay) return;
                for (var i = 0; i < _entered.Count; i++)
                {
                    _contact.Native = _entered[i];
                    Box2DFixture2D.RaiseCollisionEnter(_contact, _enteredImpulse[i]);
                }
                if (stay)
                {
                    foreach (var c in world.ContactManager.ContactList)
                    {
                        if (!c.IsTouching || !c.IsEnabled || c.FixtureA.IsSensor || c.FixtureB.IsSensor) continue;
                        if (_entered.Contains(c)) continue;
                        _contact.Native = c;
                        Box2DFixture2D.RaiseStay(_contact);
                    }
                }
                _entered.Clear();
                _enteredImpulse.Clear();
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
            private readonly bool _clip;

            public RayCastCollector(
                Dictionary<Body, Body2DAdapter> map,
                List<PhysxRaycastHit2D> hits,
                bool clip)
            {
                _map = map;
                _hits = hits;
                _clip = clip;
            }

            public float RayCastCallback(Fixture fixture, in Vector2 point, in Vector2 normal, float fraction)
            {
                // Fixtures of bodies not added to this world are ignored.
                if (!_map.TryGetValue(fixture.Body, out var adapter))
                    return -1f;
                if (_clip)
                {
                    // Closest hit only: keep the latest (each one is closer than the last) and clip.
                    _hits.Clear();
                    _hits.Add(new PhysxRaycastHit2D(adapter, point, normal, fraction));
                    return fraction;
                }
                _hits.Add(new PhysxRaycastHit2D(adapter, point, normal, fraction));
                return 1f;
            }
        }
    }
}
