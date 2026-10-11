/*
Copyright 2025 Aron Gere
Licensed under the Apache License, Version 2.0
*/

using System.Numerics;

namespace Altruist.Physx.TwoD
{
    /// <summary>
    /// A contact seen as a typed pair: <see cref="A"/> is the tag of type <typeparamref name="TA"/>,
    /// <see cref="B"/> the tag of type <typeparamref name="TB"/>, whichever fixture the engine put
    /// first. Fixtures, bodies and the normal are oriented to match: <see cref="Normal"/> points from
    /// A's side to B's side (the engine's A→B normal, negated when the pair was swapped).
    ///
    /// <para>A tag is the fixture's <see cref="Altruist.Physx.Contracts.IPhysxCollider.UserData"/>, or its body's
    /// <see cref="IPhysxBody2D.UserData"/> when the fixture has none (<see cref="ContactRouter2D.TagOf"/>).</para>
    ///
    /// <para>Valid as long as the underlying contact view is: inside the listener callback for a
    /// routed callback, until the contact list changes for an enumerated one.</para>
    /// </summary>
    public readonly struct RoutedContact2D<TA, TB>
    {
        /// <summary>The engine's contact (unoriented).</summary>
        public IPhysxContact2D Contact { get; }

        /// <summary>The tag of type <typeparamref name="TA"/>.</summary>
        public TA A { get; }

        /// <summary>The tag of type <typeparamref name="TB"/>.</summary>
        public TB B { get; }

        /// <summary>True when the engine's fixture A carries <see cref="B"/> (the pair was flipped).</summary>
        public bool Swapped { get; }

        /// <summary>Wraps a contact with its tags already ordered. Normally produced by
        /// <see cref="ContactRouter2D"/> / <see cref="ContactQueries2D"/>, not constructed by hand.</summary>
        /// <param name="contact">The engine's contact view.</param>
        /// <param name="a">The tag of type <typeparamref name="TA"/>.</param>
        /// <param name="b">The tag of type <typeparamref name="TB"/>.</param>
        /// <param name="swapped">True when <paramref name="a"/> belongs to the engine's fixture B.</param>
        public RoutedContact2D(IPhysxContact2D contact, TA a, TB b, bool swapped)
        {
            Contact = contact;
            A = a;
            B = b;
            Swapped = swapped;
        }

        /// <summary>The fixture carrying <see cref="A"/>.</summary>
        public IPhysxFixture2D FixtureA => Swapped ? Contact.FixtureB : Contact.FixtureA;

        /// <summary>The fixture carrying <see cref="B"/>.</summary>
        public IPhysxFixture2D FixtureB => Swapped ? Contact.FixtureA : Contact.FixtureB;

        /// <summary>The body on <see cref="A"/>'s side.</summary>
        public IPhysxBody2D BodyA => FixtureA.Body;

        /// <summary>The body on <see cref="B"/>'s side.</summary>
        public IPhysxBody2D BodyB => FixtureB.Body;

        /// <summary>Points in the contact's manifold (0 when not touching).</summary>
        public int PointCount => Contact.PointCount;

        /// <summary>The world manifold with the normal oriented from A's side to B's side
        /// (<c>-normal</c> when swapped: an exact sign flip). Points are as the engine reports them.</summary>
        public PhysxWorldManifold2D GetWorldManifold()
        {
            var m = Contact.GetWorldManifold();
            return Swapped ? new PhysxWorldManifold2D(-m.Normal, m.PointCount, m.Point0, m.Point1) : m;
        }

        /// <summary>The contact normal from A's side to B's side (computes the world manifold).</summary>
        public Vector2 Normal
        {
            get
            {
                var n = Contact.GetWorldManifold().Normal;
                return Swapped ? -n : n;
            }
        }

        /// <summary>False when the contact is disabled for this step.</summary>
        public bool IsEnabled => Contact.IsEnabled;

        /// <summary>The shapes touch.</summary>
        public bool IsTouching => Contact.IsTouching;

        /// <summary>Disables the contact for this step (pre-solve: one-way platforms, pass-through).</summary>
        public void Disable() => Contact.IsEnabled = false;

        /// <summary>Overrides the contact's restitution for this step (pre-solve).</summary>
        public void SetRestitution(float restitution) => Contact.Restitution = restitution;

        /// <summary>Overrides the contact's friction for this step (pre-solve).</summary>
        public void SetFriction(float friction) => Contact.Friction = friction;
    }

    /// <summary>Handles a routed contact.</summary>
    public delegate void ContactHandler2D<TA, TB>(in RoutedContact2D<TA, TB> contact);

    /// <summary>Decides whether a contact stays enabled this step: true keeps it, false disables it.</summary>
    public delegate bool ContactFilter2D<TA, TB>(in RoutedContact2D<TA, TB> contact);

    /// <summary>Handles a routed contact after solving, with the largest normal impulse applied.</summary>
    public delegate void ContactImpulseHandler2D<TA, TB>(in RoutedContact2D<TA, TB> contact, float normalImpulse);

    /// <summary>
    /// A contact listener that dispatches by the TYPES of the two tags (user data), replacing the
    /// <c>if (ua is X &amp;&amp; ub is Y) ... else if (ub is X &amp;&amp; ua is Y) ... // flip the normal</c>
    /// ladders of a hand-written listener. Register handlers per pair; the router orders each pair
    /// (the <c>TA</c> tag is always <c>A</c>) and orients the normal from A to B.
    ///
    /// <code>
    /// var router = new ContactRouter2D()
    ///     .FilterPreSolve&lt;SurfaceTag, ProjectileTag&gt;((in RoutedContact2D&lt;SurfaceTag, ProjectileTag&gt; c) =&gt; !PassThrough(c.A, c.BodyA, c.BodyB))
    ///     .OnPreSolve&lt;SurfaceTag, MoverTag&gt;((in RoutedContact2D&lt;SurfaceTag, MoverTag&gt; c) =&gt;
    ///     {
    ///         var m = c.GetWorldManifold();           // normal: surface → mover
    ///         RecordSurface(c.B.Id, c.BodyB, m.Midpoint, m.Normal);
    ///     });
    /// world.SetContactListener(router);
    /// </code>
    ///
    /// <para><b>Order</b> (deterministic, per callback):</para>
    /// <list type="number">
    /// <item>Tags are resolved (<see cref="TagOf"/>); a contact with a missing tag on either side is
    /// not routed.</item>
    /// <item>Pre-solve only: every filter whose pair matches runs, in registration order; the first
    /// that returns false disables the contact and ends the routing of this contact (no further
    /// filters or handlers).</item>
    /// <item>Pre-solve only: a contact without manifold points is not handled.</item>
    /// <item>Every handler whose pair matches runs, in registration order.</item>
    /// </list>
    /// <para><b>Matching</b>: a registration for (TA, TB) matches when fixture A's tag is a TA and
    /// fixture B's a TB (unswapped), else when fixture B's is a TA and fixture A's a TB (swapped). For
    /// TA = TB the unswapped orientation wins. Matching uses <c>is</c>, so base types and interfaces
    /// match their implementations.</para>
    /// <para>Allocation: none per contact (registrations are an array, the view is the engine's).</para>
    /// <para><b>Router vs a raw listener.</b> Use the router when dispatch depends on what the two
    /// fixtures are (their tags). Implement <see cref="IPhysxContactListener2D"/> yourself only when
    /// you need every contact regardless of tags, or the engine's raw A/B orientation. A world holds
    /// one listener (<see cref="IPhysxWorldEngine2D.SetContactListener"/>); to combine, wrap the
    /// router in your listener and forward (e.g. call <see cref="PreSolve"/>). For per-fixture
    /// enter/stay/exit events after the step use <see cref="IPhysxCollider2D.OnCollisionEnter"/> and
    /// friends instead; for polling after a step use <see cref="ContactQueries2D"/>.</para>
    /// <para>Threading: callbacks run on the thread stepping the world, inside <c>Step</c>. Register
    /// handlers before stepping; registering during a step is not synchronized. Routed contacts are
    /// views valid only inside the callback.</para>
    /// <para>The TypeScript twin is <c>@altruist/sim2d</c> <c>physics/contactRouter2D.ts</c>.</para>
    /// </summary>
    public sealed class ContactRouter2D : IPhysxContactListener2D
    {
        private Route[] _preFilters = Array.Empty<Route>();
        private Route[] _begin = Array.Empty<Route>();
        private Route[] _end = Array.Empty<Route>();
        private Route[] _preSolve = Array.Empty<Route>();
        private Route[] _postSolve = Array.Empty<Route>();

        /// <summary>The tag of a fixture: its user data, else its body's (null when neither is set).</summary>
        public static object? TagOf(IPhysxFixture2D fixture) => fixture.UserData ?? fixture.Body.UserData;

        /// <summary>Matches a contact against the pair (TA, TB) (see the class summary for the
        /// orientation rule); false when either tag is missing or the types do not match.</summary>
        public static bool TryRoute<TA, TB>(IPhysxContact2D contact, out RoutedContact2D<TA, TB> routed)
            => TryRoute(contact, TagOf(contact.FixtureA), TagOf(contact.FixtureB), out routed);

        internal static bool TryRoute<TA, TB>(IPhysxContact2D contact, object? ua, object? ub, out RoutedContact2D<TA, TB> routed)
        {
            if (ua is not null && ub is not null)
            {
                if (ua is TA a && ub is TB b)
                {
                    routed = new RoutedContact2D<TA, TB>(contact, a, b, false);
                    return true;
                }
                if (ub is TA a2 && ua is TB b2)
                {
                    routed = new RoutedContact2D<TA, TB>(contact, a2, b2, true);
                    return true;
                }
            }
            routed = default;
            return false;
        }

        /// <summary>Handles begin-contact events of the pair (TA, TB) (fired when the fixtures' bounding
        /// shapes start touching, inside the step). Returns this router for chaining.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        public ContactRouter2D OnBegin<TA, TB>(ContactHandler2D<TA, TB> handler)
        {
            _begin = Append(_begin, new HandlerRoute<TA, TB>(handler ?? throw new ArgumentNullException(nameof(handler))));
            return this;
        }

        /// <summary>Handles end-contact events of the pair (TA, TB) (also raised when a body or fixture
        /// is removed). Returns this router for chaining.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        public ContactRouter2D OnEnd<TA, TB>(ContactHandler2D<TA, TB> handler)
        {
            _end = Append(_end, new HandlerRoute<TA, TB>(handler ?? throw new ArgumentNullException(nameof(handler))));
            return this;
        }

        /// <summary>Handles pre-solve of the pair (TA, TB), after every filter kept the contact and only
        /// when it has manifold points. The handler may change restitution / friction or disable it.
        /// Pre-solve sees velocities before the solver applies the contact: the place to capture a
        /// <see cref="ContactImpact2D"/>. Returns this router for chaining.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        public ContactRouter2D OnPreSolve<TA, TB>(ContactHandler2D<TA, TB> handler)
        {
            _preSolve = Append(_preSolve, new HandlerRoute<TA, TB>(handler ?? throw new ArgumentNullException(nameof(handler))));
            return this;
        }

        /// <summary>Handles post-solve of the pair (TA, TB) with the largest normal impulse of the
        /// contact's points (mass × units/s). Returns this router for chaining.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="handler"/> is null.</exception>
        public ContactRouter2D OnPostSolve<TA, TB>(ContactImpulseHandler2D<TA, TB> handler)
        {
            _postSolve = Append(_postSolve, new ImpulseRoute<TA, TB>(handler ?? throw new ArgumentNullException(nameof(handler))));
            return this;
        }

        /// <summary>A pre-solve filter for the pair (TA, TB): returning false disables the contact for
        /// this step (one-way platforms, pass-through) and skips the rest of its routing. Filters run
        /// even for contacts without manifold points. Returns this router for chaining.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="keep"/> is null.</exception>
        public ContactRouter2D FilterPreSolve<TA, TB>(ContactFilter2D<TA, TB> keep)
        {
            _preFilters = Append(_preFilters, new FilterRoute<TA, TB>(keep ?? throw new ArgumentNullException(nameof(keep))));
            return this;
        }

        void IPhysxContactListener2D.BeginContact(IPhysxContact2D contact) => Dispatch(_begin, contact, 0f);

        void IPhysxContactListener2D.EndContact(IPhysxContact2D contact) => Dispatch(_end, contact, 0f);

        void IPhysxContactListener2D.PostSolve(IPhysxContact2D contact, float normalImpulse) => Dispatch(_postSolve, contact, normalImpulse);

        void IPhysxContactListener2D.PreSolve(IPhysxContact2D contact) => PreSolve(contact);

        /// <summary>Routes one pre-solve (also callable from a listener that wraps the router).</summary>
        public void PreSolve(IPhysxContact2D contact)
        {
            var filters = _preFilters;
            var handlers = _preSolve;
            if (filters.Length == 0 && handlers.Length == 0) return;
            var ua = TagOf(contact.FixtureA);
            var ub = TagOf(contact.FixtureB);
            if (ua is null || ub is null) return;
            for (var i = 0; i < filters.Length; i++)
            {
                if (filters[i].Invoke(contact, ua, ub, 0f) == RouteResult.Reject)
                {
                    contact.IsEnabled = false;
                    return;
                }
            }
            if (contact.PointCount == 0) return;
            for (var i = 0; i < handlers.Length; i++) handlers[i].Invoke(contact, ua, ub, 0f);
        }

        private static void Dispatch(Route[] routes, IPhysxContact2D contact, float impulse)
        {
            if (routes.Length == 0) return;
            var ua = TagOf(contact.FixtureA);
            var ub = TagOf(contact.FixtureB);
            if (ua is null || ub is null) return;
            for (var i = 0; i < routes.Length; i++) routes[i].Invoke(contact, ua, ub, impulse);
        }

        private static Route[] Append(Route[] routes, Route route)
        {
            var next = new Route[routes.Length + 1];
            Array.Copy(routes, next, routes.Length);
            next[^1] = route;
            return next;
        }

        private enum RouteResult : byte { NoMatch, Handled, Reject }

        private abstract class Route
        {
            public abstract RouteResult Invoke(IPhysxContact2D contact, object ua, object ub, float impulse);
        }

        private sealed class HandlerRoute<TA, TB> : Route
        {
            private readonly ContactHandler2D<TA, TB> _handler;
            public HandlerRoute(ContactHandler2D<TA, TB> handler) => _handler = handler;

            public override RouteResult Invoke(IPhysxContact2D contact, object ua, object ub, float impulse)
            {
                if (!TryRoute<TA, TB>(contact, ua, ub, out var routed)) return RouteResult.NoMatch;
                _handler(in routed);
                return RouteResult.Handled;
            }
        }

        private sealed class ImpulseRoute<TA, TB> : Route
        {
            private readonly ContactImpulseHandler2D<TA, TB> _handler;
            public ImpulseRoute(ContactImpulseHandler2D<TA, TB> handler) => _handler = handler;

            public override RouteResult Invoke(IPhysxContact2D contact, object ua, object ub, float impulse)
            {
                if (!TryRoute<TA, TB>(contact, ua, ub, out var routed)) return RouteResult.NoMatch;
                _handler(in routed, impulse);
                return RouteResult.Handled;
            }
        }

        private sealed class FilterRoute<TA, TB> : Route
        {
            private readonly ContactFilter2D<TA, TB> _keep;
            public FilterRoute(ContactFilter2D<TA, TB> keep) => _keep = keep;

            public override RouteResult Invoke(IPhysxContact2D contact, object ua, object ub, float impulse)
            {
                if (!TryRoute<TA, TB>(contact, ua, ub, out var routed)) return RouteResult.NoMatch;
                return _keep(in routed) ? RouteResult.Handled : RouteResult.Reject;
            }
        }
    }

    /// <summary>
    /// Typed queries over a world's current contacts, in the engine's list order (deterministic for
    /// identical worlds; see <see cref="IPhysxWorldEngine2D.ContactsOf"/> for how the world and body
    /// orders relate). Each yields only contacts that touch, are enabled and have manifold points.
    /// </summary>
    public static class ContactQueries2D
    {
        /// <summary>Touching contacts of the pair (TA, TB), oriented (A = the TA tag, normal A → B), in
        /// the world's contact list order.</summary>
        public static IEnumerable<RoutedContact2D<TA, TB>> TouchingContacts<TA, TB>(this IPhysxWorldEngine2D world)
        {
            foreach (var c in world.Contacts)
            {
                if (!c.IsTouching || !c.IsEnabled) continue;
                if (!ContactRouter2D.TryRoute<TA, TB>(c, out var routed)) continue;
                if (c.PointCount == 0) continue;
                yield return routed;
            }
        }

        /// <summary>
        /// Touching contacts of <paramref name="body"/> with fixtures tagged <typeparamref name="TOther"/>,
        /// oriented with the other side as A and this body as B (so <c>Normal</c> points from the other
        /// fixture toward this body: out of a surface, toward the body resting on it). Order: the body's
        /// contact list (= the world order restricted to the body). <c>B</c> is the body-side tag (may be null).
        /// </summary>
        public static IEnumerable<RoutedContact2D<TOther, object?>> TouchingContactsOf<TOther>(this IPhysxWorldEngine2D world, IPhysxBody2D body)
        {
            foreach (var c in world.ContactsOf(body))
            {
                if (!c.IsTouching || !c.IsEnabled) continue;
                var bodyIsA = ReferenceEquals(c.FixtureA.Body, body);
                var other = ContactRouter2D.TagOf(bodyIsA ? c.FixtureB : c.FixtureA);
                if (other is not TOther o) continue;
                if (c.PointCount == 0) continue;
                var self = ContactRouter2D.TagOf(bodyIsA ? c.FixtureA : c.FixtureB);
                // Other side = A: swapped when this body is the engine's fixture A.
                yield return new RoutedContact2D<TOther, object?>(c, o, self, bodyIsA);
            }
        }
    }
}
