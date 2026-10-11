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
        /// <inheritdoc/>
        public IPhysxWorldEngine2D Create(Vector2 gravity, float fixedDeltaTime = 1f / 60f)
            => new Box2DWorldEngine2D(gravity, fixedDeltaTime);

        /// <inheritdoc/>
        public IPhysxWorldEngine2D Create(PhysxWorldSettings2D settings) => new Box2DWorldEngine2D(settings);
    }

    /// <summary>
    /// The Box2D (Box2DSharp, Box2D 2.4 port) implementation of <see cref="IPhysxWorldEngine2D"/>. Create
    /// it through <see cref="PhysxWorldEngine2D.Create"/> or <see cref="IPhysxWorldEngineFactory2D"/>.
    /// <para>Contact events: the installed <see cref="IPhysxContactListener2D"/> gets Box2D's callbacks
    /// inside the step through one reused contact view (no allocation per contact). Fixture events:
    /// trigger enter/exit inside the step; collision enter after the step that began the touch, with
    /// the normal impulse summed over that step's post-solves; collision stay after each step (only
    /// while some fixture subscribes); collision exit inside the step. A collision that begins and
    /// ends within one step still raises enter before exit.</para>
    /// <para>Contact views from <see cref="Contacts"/> are invalidated by a step, body removal, body
    /// disable, fixture destruction or <see cref="Dispose"/> (they throw
    /// <see cref="InvalidOperationException"/> afterwards).</para>
    /// <para>Threading: see <see cref="IPhysxWorldEngine2D"/> and <see cref="Box2DThreading"/>
    /// (installed by the static constructor).</para>
    /// </summary>
    public sealed class Box2DWorldEngine2D : IPhysxWorldEngine2D
    {
        /// <inheritdoc/>
        public float FixedDeltaTime { get; private set; }

        /// <summary>The settings in effect (constructor or last <see cref="ApplySettings"/>).</summary>
        public PhysxWorldSettings2D Settings { get; private set; }

        /// <summary>A live, read-only view of the added bodies (no allocation per call). Adding or removing
        /// bodies while enumerating it throws; copy it first (<c>Bodies.ToList()</c>) to remove bodies in a loop.</summary>
        public IReadOnlyCollection<IPhysxBody> Bodies => _bodies.Values;

        internal World World => _world;

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

        // Counts internal steps, so a compound collider raises at most one stay per other collider and step.
        private int _stepIndex;

        /// <summary>A world with <paramref name="gravity"/> and <paramref name="fixedDeltaTime"/>; other settings default.</summary>
        /// <param name="gravity">World gravity in units/s² (+Y up).</param>
        /// <param name="fixedDeltaTime">Fixed step in seconds (only used with fixed stepping).</param>
        public Box2DWorldEngine2D(
            Vector2 gravity,
            float fixedDeltaTime = 1f / 60f
        ) : this(new PhysxWorldSettings2D { Gravity = gravity, FixedDeltaTime = fixedDeltaTime })
        {
        }

        static Box2DWorldEngine2D() => Box2DThreading.EnsureInstalled();

        /// <summary>A world with every setting.</summary>
        /// <param name="settings">Gravity, step and solver settings.</param>
        public Box2DWorldEngine2D(PhysxWorldSettings2D settings)
        {
            Settings = settings;
            FixedDeltaTime = settings.FixedDeltaTime;
            _world = new World(settings.Gravity);
            _contacts = new ContactBridge();
            _world.SetContactListener(_contacts);
        }

        /// <summary>Replaces the settings, sets the world's gravity and resets the fixed-step accumulator.</summary>
        /// <param name="settings">The new settings.</param>
        public void ApplySettings(PhysxWorldSettings2D settings)
        {
            Settings = settings;
            FixedDeltaTime = settings.FixedDeltaTime;
            _world.Gravity = settings.Gravity;
            _accumulator = 0f;
        }

        /// <inheritdoc/>
        public void Step(float deltaTime)
        {
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
            _stepIndex++;
            _world.Step(dt, Settings.VelocityIterations, Settings.PositionIterations);
            _contacts.AfterStep(_world, StaySubscribers > 0, _stepIndex);
        }

        /// <inheritdoc/>
        public IPhysxBody2D AddBody(IPhysxBody2D body)
        {
            if (body is not Body2DAdapter adapter)
                throw new InvalidOperationException("This engine can only add bodies created by the Box2D provider.");

            _bodies[adapter.Id] = adapter;
            _byNative[adapter.Underlying] = adapter;
            adapter.Engine = this;
            return adapter;
        }

        /// <inheritdoc/>
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

        /// <inheritdoc/>
        public IPhysxFixture2D CreateFixture(IPhysxBody2D body, in PhysxFixtureDef2D def)
        {
            if (body is not Body2DAdapter owner)
                throw new InvalidOperationException("Body must be created by this engine.");
            var fixture = CreateNativeFixture(owner, def);
            owner.AddCollider(fixture);
            return fixture;
        }

        /// <summary>Creates the fixture without recording it in the body's collider list (a
        /// <see cref="Collider2D"/> records itself instead of its fixtures).</summary>
        internal Box2DFixture2D CreateNativeFixture(Body2DAdapter owner, in PhysxFixtureDef2D def)
        {
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
            return fixture;
        }

        /// <inheritdoc/>
        public void RemoveBody(IPhysxBody body)
        {
            if (body is Body2DAdapter b && _bodies.Remove(b.Id))
            {
                _byNative.Remove(b.Underlying);
                ContactGeneration++;
                _world.DestroyBody(b.Underlying);
            }
        }

        /// <inheritdoc/>
        public void SetContactListener(IPhysxContactListener2D? listener) => _contacts.Listener = listener;

        /// <summary>
        /// The closest <paramref name="maxHits"/> (at least 1) distinct bodies of this world along the ray,
        /// sorted by fraction, then by body id (ordinal) on equal fractions, so the result does not depend on
        /// the order Box2D reports hits in. Sensors are skipped; a body with several fixtures appears once, at
        /// its closest hit. For per-fixture hits or sensors use
        /// <see cref="RayCast(Vector2,Vector2,IPhysxRayCastCallback2D)"/>.
        /// </summary>
        public IEnumerable<PhysxRaycastHit2D> RayCast(PhysxRay2D ray, int maxHits = 1)
        {
            maxHits = Math.Max(1, maxHits);
            var cb = new RayCastCollector(_byNative, clip: maxHits == 1);
            _world.RayCast(cb, ray.From, ray.To);
            var hits = cb.ClosestPerBody.Values.ToList();
            hits.Sort(CompareHits);
            if (hits.Count > maxHits)
                hits.RemoveRange(maxHits, hits.Count - maxHits);
            return hits;
        }

        private static int CompareHits(PhysxRaycastHit2D a, PhysxRaycastHit2D b)
        {
            var byFraction = a.Fraction.CompareTo(b.Fraction);
            return byFraction != 0 ? byFraction : string.CompareOrdinal(a.Body.Id, b.Body.Id);
        }

        /// <inheritdoc/>
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

        /// <summary>Destroys every added body (contact views become invalid). The world object itself
        /// stays usable but empty.</summary>
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
            public void AfterStep(World world, bool stay, int step)
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
                        Box2DFixture2D.RaiseStay(_contact, step);
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
            private readonly bool _clip;

            // Each body's closest hit (ties on fraction: the lower point, by X then Y, so the order of
            // Box2D's callbacks does not matter).
            public readonly Dictionary<Body2DAdapter, PhysxRaycastHit2D> ClosestPerBody = new();

            public RayCastCollector(Dictionary<Body, Body2DAdapter> map, bool clip)
            {
                _map = map;
                _clip = clip;
            }

            public float RayCastCallback(Fixture fixture, in Vector2 point, in Vector2 normal, float fraction)
            {
                // Sensors and fixtures of bodies not added to this world are ignored.
                if (fixture.IsSensor || !_map.TryGetValue(fixture.Body, out var adapter))
                    return -1f;
                var hit = new PhysxRaycastHit2D(adapter, point, normal, fraction);
                if (!ClosestPerBody.TryGetValue(adapter, out var best) || IsBefore(hit, best))
                    ClosestPerBody[adapter] = hit;
                // Closest body only: clip the ray here. Box2D still reports hits at exactly this
                // fraction, so ties are all seen and broken by body id afterwards.
                return _clip ? fraction : 1f;
            }

            private static bool IsBefore(in PhysxRaycastHit2D a, in PhysxRaycastHit2D b) =>
                a.Fraction < b.Fraction
                || (a.Fraction == b.Fraction && (a.Point.X < b.Point.X || (a.Point.X == b.Point.X && a.Point.Y < b.Point.Y)));
        }
    }
}
