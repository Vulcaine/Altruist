// PhysxWorld2D.cs
using System.Numerics;

using Altruist.Physx.Contracts;
using Altruist.Physx.TwoD;

namespace Altruist.Physx
{

    /// <summary>
    /// A 2D physics world (Box2D-backed: <see cref="Box2DWorldEngine2D"/>). The main entry point for
    /// standalone simulations: create bodies and fixtures, install a contact listener, step, query
    /// contacts and rays.
    /// <para>Conventions: world units, +Y up (gravity is usually <c>(0, -g)</c>), radians
    /// counter-clockwise, seconds.</para>
    /// <para>Threading: not thread-safe. One thread at a time steps and touches a world (and its bodies,
    /// fixtures, contacts); different worlds may run on different threads at once when
    /// <see cref="PhysxWorldEngine2D.ParallelWorldsSupported"/> is true (see <see cref="Box2DThreading"/>).</para>
    /// <para>Determinism: identical worlds built and stepped identically on the same runtime produce
    /// identical bits (float32 Box2D, deterministic contact order). Contact, ray-cast and per-body
    /// contact orders follow the engine's internal lists.</para>
    /// <example><code>
    /// using var world = PhysxWorldEngine2D.Create(new PhysxWorldSettings2D { Gravity = new(0, -9.81f) });
    /// var ground = world.CreateBody(new PhysxBodyDef2D());   // static
    /// world.CreateFixture(ground, new PhysxFixtureDef2D { Shape = PhysxShape2D.Edge(new(-10, 0), new(10, 0)) });
    /// var body = world.CreateBody(new PhysxBodyDef2D { Type = PhysxBodyType.Dynamic, Position = new(0, 4) });
    /// world.CreateFixture(body, new PhysxFixtureDef2D { Shape = PhysxShape2D.Circle(0.5f), Density = 1f, UserData = myTag });
    /// world.SetContactListener(new ContactRouter2D().OnBegin&lt;MyTag, object&gt;((in RoutedContact2D&lt;MyTag, object&gt; c) =&gt; { }));
    /// for (var i = 0; i &lt; 60; i++) world.Step(1f / 60f);
    /// </code></example>
    /// </summary>
    public interface IPhysxWorldEngine2D : IDisposable
    {
        /// <summary>The fixed step from the settings (seconds); used by <see cref="Step"/> only with
        /// <see cref="PhysxWorldSettings2D.MaxSubSteps"/> above zero.</summary>
        float FixedDeltaTime { get; }

        /// <summary>The bodies added to this world (a snapshot copy on Box2D: allocates per call).</summary>
        IReadOnlyCollection<IPhysxBody> Bodies { get; }

        /// <summary>
        /// Advances the world by <paramref name="deltaTime"/> in one step, or, with fixed stepping
        /// (<see cref="PhysxWorldSettings2D.MaxSubSteps"/> above zero), in whole steps of
        /// <see cref="FixedDeltaTime"/> (see there).
        /// </summary>
        /// <remarks>Contact listener callbacks and trigger events run inside this call; collision
        /// enter / stay events run at the end of each internal step. Box2D: a world with no bodies does
        /// not step (and does not advance its accumulator).</remarks>
        void Step(float deltaTime);

        /// <summary>Registers a body created by this engine's provider (not needed after
        /// <see cref="CreateBody"/>); returns it.</summary>
        /// <exception cref="InvalidOperationException">Box2D: the body was not created by the Box2D provider.</exception>
        IPhysxBody2D AddBody(IPhysxBody2D body);

        /// <summary>Removes and destroys a body of this world (end-contact callbacks fire for its touching contacts; contact views
        /// handed out earlier become invalid). No-op for bodies not in this world.</summary>
        void RemoveBody(IPhysxBody body);

        /// <summary>The closest <paramref name="maxHits"/> bodies along the ray, closest first.
        /// Body-level and unfiltered (sensors included); one entry per fixture hit, so a body with
        /// several fixtures can appear more than once. For per-fixture filtering use
        /// <see cref="RayCast(Vector2,Vector2,IPhysxRayCastCallback2D)"/> with a
        /// <see cref="ClosestRayHit2D"/>. Allocates the result list.</summary>
        IEnumerable<PhysxRaycastHit2D> RayCast(PhysxRay2D ray, int maxHits = 1);

        /// <summary>
        /// Applies world settings after creation (used by the default
        /// <see cref="IPhysxWorldEngineFactory2D.Create(PhysxWorldSettings2D)"/>). Engines that
        /// cannot change their settings ignore it.
        /// </summary>
        void ApplySettings(PhysxWorldSettings2D settings) { }

        /// <summary>Creates a body in this world (already added: no <see cref="AddBody"/> needed).
        /// Add shapes with <see cref="CreateFixture"/>. Must not be called from inside a contact callback (the world is locked during the step).</summary>
        /// <param name="def">Type, transform, flags and user data; <c>default</c> is a static body at the origin.</param>
        IPhysxBody2D CreateBody(in PhysxBodyDef2D def);

        /// <summary>Attaches a shape with its material and filter to a body of this world (updates the
        /// body's mass from the density).</summary>
        /// <param name="body">A body created by this engine.</param>
        /// <param name="def">Shape, material, filter, sensor flag and user data (the routing tag).</param>
        /// <exception cref="InvalidOperationException">Box2D: the body was not created by this engine type.</exception>
        IPhysxFixture2D CreateFixture(IPhysxBody2D body, in PhysxFixtureDef2D def);

        /// <summary>Receives this world's contact callbacks during <see cref="Step"/> (null removes it).
        /// One listener per world; it replaces the previous one. Usually a <see cref="ContactRouter2D"/>.</summary>
        /// <param name="listener">The listener, or null.</param>
        void SetContactListener(IPhysxContactListener2D? listener);

        /// <summary>Casts a ray; the callback decides per hit whether to ignore, clip or stop
        /// (see <see cref="IPhysxRayCastCallback2D"/>). Fixture-level, every fixture of the world
        /// (sensors included unless the callback filters them). Allocation-free on Box2D except for
        /// nested casts from inside a callback.</summary>
        /// <param name="from">Ray start (world).</param>
        /// <param name="to">Ray end (world).</param>
        /// <param name="callback">Receives each hit, e.g. a <see cref="ClosestRayHit2D"/>.</param>
        void RayCast(Vector2 from, Vector2 to, IPhysxRayCastCallback2D callback);

        /// <summary>
        /// The world's current contacts (touching or only overlapping bounding boxes; check
        /// <see cref="IPhysxContact2D.IsTouching"/>). Each item is a distinct view, valid until the
        /// world's contact list changes (the next step, removing or disabling a body); copy it with
        /// <see cref="IPhysxContact2D.ToInfo"/> to keep it longer.
        /// </summary>
        IEnumerable<IPhysxContact2D> Contacts { get; }

        /// <summary>
        /// The current contacts of one body, each a view like the items of <see cref="Contacts"/>, in
        /// the order of <see cref="Contacts"/> restricted to this body. Box2D (and planck.js) link a new
        /// contact first both in the world's list and in each body's list, so this is also the order of
        /// the engine's per-body contact list (planck's <c>body.getContactList()</c>): code that walks a
        /// body's list on a client and code that filters the world list on the server see the same
        /// sequence.
        /// </summary>
        /// <param name="body">The body whose contacts to list.</param>
        IEnumerable<IPhysxContact2D> ContactsOf(IPhysxBody2D body)
        {
            foreach (var c in Contacts)
                if (ReferenceEquals(c.BodyA, body) || ReferenceEquals(c.BodyB, body))
                    yield return c;
        }
    }

    /// <summary>
    /// DI service that creates 2D worlds (<see cref="WorldEngineFactory2D"/>, registered in every
    /// environment mode). Inject it where a service creates worlds; the static
    /// <see cref="PhysxWorldEngine2D.Create"/> does the same without DI.
    /// </summary>
    public interface IPhysxWorldEngineFactory2D
    {
        /// <summary>A world with the given gravity (units/s², +Y up) and fixed step; other settings default.</summary>
        /// <param name="gravity">World gravity, e.g. <c>(0, -9.81)</c>.</param>
        /// <param name="fixedDeltaTime">Fixed step in seconds (only used with fixed stepping).</param>
        IPhysxWorldEngine2D Create(Vector2 gravity, float fixedDeltaTime = 1f / 60f);

        /// <summary>
        /// A world with every setting. The default creates it with gravity and fixed dt and
        /// applies the rest (<see cref="IPhysxWorldEngine2D.ApplySettings"/>).
        /// </summary>
        /// <param name="settings">All world settings.</param>
        IPhysxWorldEngine2D Create(PhysxWorldSettings2D settings)
        {
            var engine = Create(settings.Gravity, settings.FixedDeltaTime);
            engine.ApplySettings(settings);
            return engine;
        }
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
        /// <summary>Creates a new, empty Box2D world with <paramref name="settings"/>. Each world is
        /// independent; dispose it to destroy its bodies.</summary>
        /// <param name="settings">Gravity, step and solver settings.</param>
        public static IPhysxWorldEngine2D Create(PhysxWorldSettings2D settings) => new Box2DWorldEngine2D(settings);

        /// <summary>Independent worlds may be stepped on different threads at the same time. Calling it
        /// installs the per-thread contact pools once (<see cref="Box2DThreading.EnsureInstalled"/>);
        /// false means stepping several worlds concurrently is unsafe (see
        /// <see cref="Box2DThreading.FailureReason"/>).</summary>
        public static bool ParallelWorldsSupported => Box2DThreading.EnsureInstalled();
    }

    /// <summary>
    /// <see cref="IPhysxWorld2D"/> facade over an <see cref="IPhysxWorldEngine2D"/>, used by the
    /// game-world organizer. Every call forwards to the engine; use the engine directly for contacts,
    /// listeners, fixture definitions and fixture-level ray casts.
    /// </summary>
    public sealed class PhysxWorld2D : IPhysxWorld2D, IDisposable
    {
        /// <summary>The engine's bodies (<see cref="IPhysxWorldEngine2D.Bodies"/>).</summary>
        public IReadOnlyCollection<IPhysxBody> Bodies => _engine.Bodies;

        private readonly IPhysxWorldEngine2D _engine;

        /// <summary>Wraps <paramref name="engine"/> (owned: <see cref="Dispose"/> disposes it).</summary>
        /// <param name="engine">The engine to forward to.</param>
        /// <exception cref="ArgumentNullException"><paramref name="engine"/> is null.</exception>
        public PhysxWorld2D(IPhysxWorldEngine2D engine)
        {
            _engine = engine ?? throw new ArgumentNullException(nameof(engine));
        }

        /// <summary>Steps the engine (<see cref="IPhysxWorldEngine2D.Step"/>).</summary>
        /// <param name="deltaTime">Seconds to advance.</param>
        public void Step(float deltaTime) => _engine.Step(deltaTime);

        /// <inheritdoc/>
        public void AddBody(IPhysxBody2D body) => _engine.AddBody(body);

        /// <inheritdoc/>
        public void RemoveBody(IPhysxBody body) => _engine.RemoveBody(body);

        /// <inheritdoc/>
        public IEnumerable<PhysxRaycastHit2D> RayCast(PhysxRay2D ray, int maxHits = 1)
            => _engine.RayCast(ray, maxHits);

        /// <summary>Disposes the engine (destroys its bodies).</summary>
        public void Dispose() => _engine.Dispose();
    }
}
