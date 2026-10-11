/**
 * Physics layer. Contacts dispatched by the kinds of their two tags — mirror of C#
 * `ContactRouter2D`, `RoutedContact2D<TA, TB>` and `ContactQueries2D`. TypeScript has no runtime
 * types for tags, so a pair is given by two type guards (`isSurface`, `isMover`, ...) instead of two
 * type parameters. A planck.js `World` / `Contact` / `Fixture` / `Body` satisfies the structural
 * interfaces below as is.
 *
 * Tag: the fixture's user data, else its body's. Orientation: `a` is the tag matching the first
 * guard; fixtures, bodies and the normal follow (`normal` points from a's side to b's side: the
 * manifold's A→B normal, negated when the pair was swapped). The point is the average of the
 * manifold points. Matching tries the unswapped orientation first.
 *
 * Pre-solve order per contact: tags resolved (a missing tag ⇒ not routed) → every matching filter
 * in registration order (the first `false` disables the contact and ends its routing) → no manifold
 * points ⇒ not handled → every matching handler in registration order.
 */
import type { Vec2Like } from '../math/vec2.ts';

/** Structural fixture (planck `Fixture`): its user data and its body. TS view of C# `IPhysxFixture2D`.
 * @typeParam B - The engine's body type. */
export interface ContactFixtureLike<B = ContactBodyLike> {
  /** The fixture's user data (tag), or null / undefined when unset. C# `IPhysxFixture2D.UserData`. */
  getUserData(): unknown;
  /** The body owning the fixture. C# `IPhysxFixture2D.Body`. */
  getBody(): B;
}

/** Structural body as seen by contact routing: only its user data. TS view of C# `IPhysxBody2D.UserData`. */
export interface ContactBodyLike {
  /** The body's user data (tag), used when the fixture has none. */
  getUserData(): unknown;
}

/** World-space manifold of a contact (planck `WorldManifold`). TS view of C# `PhysxWorldManifold2D`. */
export interface WorldManifoldLike {
  /** Unit normal from the engine's fixture A to fixture B. */
  normal: Vec2Like;
  /** Contact points in world space; only the first `pointCount` are valid. */
  points: readonly Vec2Like[];
  /** Number of valid points (0, 1 or 2). */
  pointCount: number;
}

/** Structural contact (planck `Contact`). TS view of C# `IPhysxContact2D`.
 * @typeParam F - The engine's fixture type. */
export interface ContactLike<F extends ContactFixtureLike = ContactFixtureLike> {
  /** The engine's first fixture (raw orientation). C# `IPhysxContact2D.FixtureA`. */
  getFixtureA(): F;
  /** The engine's second fixture (raw orientation). C# `IPhysxContact2D.FixtureB`. */
  getFixtureB(): F;
  /** Whether the shapes actually touch (manifold has points). C# `IPhysxContact2D.IsTouching`. */
  isTouching(): boolean;
  /** False when disabled for this step. C# `IPhysxContact2D.IsEnabled` (getter). */
  isEnabled(): boolean;
  /** Enables / disables the contact for the current step (call in pre-solve). C# `IPhysxContact2D.IsEnabled` (setter). */
  setEnabled(flag: boolean): void;
  /** Overrides the restitution of this contact for the step. C# `IPhysxContact2D.Restitution`. */
  setRestitution(restitution: number): void;
  /** Overrides the friction of this contact for the step. C# `IPhysxContact2D.Friction`. */
  setFriction(friction: number): void;
  /** Computes the world manifold (planck accepts `null` to allocate a new one). C#
   * `IPhysxContact2D.GetWorldManifold`. */
  getWorldManifold(out: null): WorldManifoldLike | undefined | null;
}

/** A contact in the world's singly linked contact list (planck `Contact.getNext()`).
 * @typeParam C - The engine's contact type. */
export interface ContactListLike<C> {
  /** The next contact in the world list, or null at the end. */
  getNext(): C | null;
}

/** A world whose contacts can be walked (planck `World.getContactList()` / `Contact.getNext()`). */
export interface ContactWorldLike<C extends ContactLike & ContactListLike<C>> {
  /** Head of the world's contact list (null when empty). */
  getContactList(): C | null;
}

/** A world that emits pre-solve (planck `world.on('pre-solve', fn)`). Not re-exported from the
 * package index. */
export interface PreSolveWorldLike<C> {
  /** Subscribes `listener` to the world's pre-solve event. */
  on(event: 'pre-solve', listener: (contact: C) => void): unknown;
}

/** A body whose contact edges can be walked (planck `Body.getContactList()`). */
export interface ContactEdgeBodyLike<C> {
  /** Head of the body's contact-edge list (null when the body has no contacts). */
  getContactList(): ContactEdgeLike<C> | null;
}

/** One edge of a body's contact list (planck `ContactEdge`). */
export interface ContactEdgeLike<C> {
  /** The contact this edge belongs to. */
  contact: C;
  /** Next edge of the same body, or null / undefined at the end. */
  next: ContactEdgeLike<C> | null | undefined;
}

/** Type guard that recognises one kind of tag (fixture or body user data). The TS stand-in for a C#
 * type parameter `TA` / `TB` matched with `is`: e.g. `(t): t is SurfaceTag => t instanceof SurfaceTag`. */
export type TagGuard<T> = (tag: unknown) => tag is T;

/**
 * A contact seen as a typed pair. One view object is reused per router / query: valid only inside
 * the callback that received it (copy what you keep). Mirrors the C# readonly struct
 * `RoutedContact2D<TA, TB>`; unlike C# (which computes `Normal` on demand), `point` / `normal` here
 * are precomputed fields.
 */
export class RoutedContact2D<TA, TB, C extends ContactLike = ContactLike> {
  /** The engine contact (raw orientation). C# `RoutedContact2D.Contact`. */
  contact!: C;
  /** The tag that matched the first guard. C# `RoutedContact2D.A`. */
  a!: TA;
  /** The tag that matched the second guard. C# `RoutedContact2D.B`. */
  b!: TB;
  /** True when the engine's fixture A carries `b`. */
  swapped = false;
  /** Manifold point count (0 when not computed: begin / end / post-solve routes and filters never
   * compute it). Note C# `RoutedContact2D.PointCount` reads the live contact instead. */
  pointCount = 0;
  /** Average of the manifold points (world space). Only filled for pre-solve handlers and the
   * touching-contact queries; otherwise left at its last value (initially zero). */
  readonly point = { x: 0, y: 0 };
  /** Unit contact normal from a's side to b's side (an exact sign flip of the manifold normal when
   * swapped). Filled under the same conditions as {@link point}. C# `RoutedContact2D.Normal`. */
  readonly normal = { x: 0, y: 0 };

  /** The fixture carrying `a` (the engine's B when swapped). C# `RoutedContact2D.FixtureA`. */
  get fixtureA(): ReturnType<C['getFixtureA']> {
    return (this.swapped ? this.contact.getFixtureB() : this.contact.getFixtureA()) as ReturnType<C['getFixtureA']>;
  }

  /** The fixture carrying `b`. C# `RoutedContact2D.FixtureB`. */
  get fixtureB(): ReturnType<C['getFixtureA']> {
    return (this.swapped ? this.contact.getFixtureA() : this.contact.getFixtureB()) as ReturnType<C['getFixtureA']>;
  }

  /** Body of {@link fixtureA}. C# `RoutedContact2D.BodyA`. */
  get bodyA(): ReturnType<ReturnType<C['getFixtureA']>['getBody']> {
    return this.fixtureA.getBody() as ReturnType<ReturnType<C['getFixtureA']>['getBody']>;
  }

  /** Body of {@link fixtureB}. C# `RoutedContact2D.BodyB`. */
  get bodyB(): ReturnType<ReturnType<C['getFixtureA']>['getBody']> {
    return this.fixtureB.getBody() as ReturnType<ReturnType<C['getFixtureA']>['getBody']>;
  }

  /** Disables the contact for this step (call from pre-solve). C# `RoutedContact2D.Disable`. */
  disable(): void {
    this.contact.setEnabled(false);
  }

  /** Overrides this contact's restitution for the step (pre-solve). C# `RoutedContact2D.SetRestitution`. */
  setRestitution(restitution: number): void {
    this.contact.setRestitution(restitution);
  }

  /** Overrides this contact's friction for the step (pre-solve). C# `RoutedContact2D.SetFriction`. */
  setFriction(friction: number): void {
    this.contact.setFriction(friction);
  }

  /** @internal Fills point / normal from a world manifold (same arithmetic as the inline form). */
  setGeometry(wm: WorldManifoldLike): void {
    const n = wm.pointCount;
    let px = 0;
    let py = 0;
    for (let i = 0; i < n; i++) {
      px += wm.points[i]!.x;
      py += wm.points[i]!.y;
    }
    if (n > 0) {
      px /= n;
      py /= n;
    }
    this.pointCount = n;
    this.point.x = px;
    this.point.y = py;
    this.normal.x = this.swapped ? -wm.normal.x : wm.normal.x;
    this.normal.y = this.swapped ? -wm.normal.y : wm.normal.y;
  }
}

/** The tag of a fixture: its user data, else its body's (`??`, so null / undefined fall through).
 * Mirrors C# `ContactRouter2D.TagOf`. */
export function tagOf(fixture: ContactFixtureLike): unknown {
  return fixture.getUserData() ?? fixture.getBody().getUserData();
}

/**
 * Orients a contact for the pair (isA, isB) into `view`; false when it does not match or either tag
 * is null / undefined. Tries the unswapped orientation first. Sets `contact`, `a`, `b`, `swapped`
 * only (not `point` / `normal`). Mirrors C# `ContactRouter2D.TryRoute` (which takes type
 * parameters and returns the view through `out`). Use it inside a raw listener; prefer
 * {@link ContactRouter2D} for registration-based dispatch.
 * @param ua - Tag of fixture A (defaults to {@link tagOf}; pass it when already resolved).
 * @param ub - Tag of fixture B (defaults to {@link tagOf}).
 */
export function tryRoute<TA, TB, C extends ContactLike>(
  contact: C,
  isA: TagGuard<TA>,
  isB: TagGuard<TB>,
  view: RoutedContact2D<TA, TB, C>,
  ua: unknown = tagOf(contact.getFixtureA()),
  ub: unknown = tagOf(contact.getFixtureB()),
): boolean {
  if (ua == null || ub == null) return false;
  if (isA(ua) && isB(ub)) {
    view.contact = contact;
    view.a = ua;
    view.b = ub;
    view.swapped = false;
    return true;
  }
  if (isA(ub) && isB(ua)) {
    view.contact = contact;
    view.a = ub;
    view.b = ua;
    view.swapped = true;
    return true;
  }
  return false;
}

interface Route<C extends ContactLike> {
  /** 0 = no match, 1 = handled, 2 = reject (filters). */
  invoke(contact: C, ua: unknown, ub: unknown, wm: WorldManifoldLike | null, impulse: number): 0 | 1 | 2;
}

/**
 * Contact listener that dispatches by the kinds of the two tags (user data), replacing
 * `if (isX(ua) && isY(ub)) ... else if (isX(ub) && isY(ua)) ... // flip the normal` ladders in a
 * hand-written listener. Register handlers per pair; the router orders each pair (the `isA` tag is
 * always `a`) and orients the normal from a to b. Mirrors C# `ContactRouter2D`.
 *
 * Order per callback (deterministic): tags resolved with {@link tagOf} (missing ⇒ not routed) →
 * pre-solve only: matching filters in registration order, the first `false` disables the contact
 * and ends its routing → pre-solve only: no manifold points ⇒ not handled → every matching handler
 * in registration order. Each registration reuses one {@link RoutedContact2D} view.
 *
 * Router vs raw world listeners: use the router when dispatch depends on what the two fixtures are.
 * Subscribe to `world.on(...)` yourself only when you need every contact regardless of tags or the
 * engine's raw A/B orientation; you can also forward from your own listener to
 * {@link ContactRouter2D.preSolve} etc. instead of {@link ContactRouter2D.attach}. For polling after
 * a step use {@link touchingContacts} / {@link touchingContactsOf}.
 * @typeParam C - The engine's contact type (planck `Contact`).
 * @example
 * ```ts
 * const router = new ContactRouter2D<Contact>()
 *   .filterPreSolve(isSurface, isProjectile, (c) => !passesThrough(c.a, c.bodyB))
 *   .onPreSolve(isSurface, isMover, (c) => recordSurface(c.b, c.point, c.normal)) // normal: surface → mover
 *   .onPostSolve(isMover, isMover, (c, impulse) => hits.push(impulse))
 *   .attach(world);
 * ```
 */
export class ContactRouter2D<C extends ContactLike = ContactLike> {
  private readonly filters: Route<C>[] = [];
  private readonly preSolveRoutes: Route<C>[] = [];
  private readonly beginRoutes: Route<C>[] = [];
  private readonly endRoutes: Route<C>[] = [];
  private readonly postSolveRoutes: Route<C>[] = [];

  /** Subscribes to a planck world's contact events (`pre-solve`, `begin-contact`, `end-contact`,
   * `post-solve`; the post-solve impulse becomes the largest of `normalImpulses`, 0 when absent).
   * Call once; calling twice dispatches every event twice. The C# twin is installed with
   * `world.SetContactListener(router)` instead. @returns this, for chaining. */
  attach(world: { on(event: string, listener: (contact: C, extra?: unknown) => void): unknown }): this {
    world.on('pre-solve', (c) => this.preSolve(c));
    world.on('begin-contact', (c) => this.beginContact(c));
    world.on('end-contact', (c) => this.endContact(c));
    world.on('post-solve', (c, impulse) => this.postSolve(c, maxNormalImpulse(impulse)));
    return this;
  }

  /** Registers a pre-solve filter for the pair: returning false disables the contact for the step and
   * skips the rest of its routing (later filters and all pre-solve handlers). Filters see no manifold
   * (`point` / `normal` not filled). Mirrors C# `ContactRouter2D.FilterPreSolve`. @returns this. */
  filterPreSolve<TA, TB>(isA: TagGuard<TA>, isB: TagGuard<TB>, keep: (c: RoutedContact2D<TA, TB, C>) => boolean): this {
    this.filters.push(route(isA, isB, (v) => (keep(v) ? 1 : 2)));
    return this;
  }

  /** Registers a pre-solve handler for the pair; runs after the filters, only for contacts with
   * manifold points, with `point` / `normal` filled. Mirrors C# `ContactRouter2D.OnPreSolve`. @returns this. */
  onPreSolve<TA, TB>(isA: TagGuard<TA>, isB: TagGuard<TB>, handler: (c: RoutedContact2D<TA, TB, C>) => void): this {
    this.preSolveRoutes.push(route(isA, isB, (v) => (handler(v), 1)));
    return this;
  }

  /** Registers a begin-contact handler for the pair (no manifold: `point` / `normal` not filled).
   * Mirrors C# `ContactRouter2D.OnBegin`. @returns this. */
  onBegin<TA, TB>(isA: TagGuard<TA>, isB: TagGuard<TB>, handler: (c: RoutedContact2D<TA, TB, C>) => void): this {
    this.beginRoutes.push(route(isA, isB, (v) => (handler(v), 1)));
    return this;
  }

  /** Registers an end-contact handler for the pair (no manifold). Mirrors C# `ContactRouter2D.OnEnd`.
   * @returns this. */
  onEnd<TA, TB>(isA: TagGuard<TA>, isB: TagGuard<TB>, handler: (c: RoutedContact2D<TA, TB, C>) => void): this {
    this.endRoutes.push(route(isA, isB, (v) => (handler(v), 1)));
    return this;
  }

  /** Registers a post-solve handler for the pair; receives the largest normal impulse of the contact
   * (no manifold: `point` / `normal` not filled). Mirrors C# `ContactRouter2D.OnPostSolve`. @returns this. */
  onPostSolve<TA, TB>(isA: TagGuard<TA>, isB: TagGuard<TB>, handler: (c: RoutedContact2D<TA, TB, C>, impulse: number) => void): this {
    this.postSolveRoutes.push(route(isA, isB, (v, impulse) => (handler(v, impulse), 1)));
    return this;
  }

  /** Routes one pre-solve (also callable from a listener that wraps the router). Mirrors C#
   * `ContactRouter2D.PreSolve`. */
  preSolve(contact: C): void {
    if (this.filters.length === 0 && this.preSolveRoutes.length === 0) return;
    const ua = tagOf(contact.getFixtureA());
    const ub = tagOf(contact.getFixtureB());
    if (ua == null || ub == null) return;
    for (const f of this.filters) {
      if (f.invoke(contact, ua, ub, null, 0) === 2) {
        contact.setEnabled(false);
        return;
      }
    }
    if (this.preSolveRoutes.length === 0) return;
    const wm = contact.getWorldManifold(null);
    if (!wm || wm.pointCount === 0) return;
    for (const h of this.preSolveRoutes) h.invoke(contact, ua, ub, wm, 0);
  }

  /** Routes one begin-contact. Mirrors C# `ContactRouter2D`'s explicit `IPhysxContactListener2D.BeginContact`. */
  beginContact(contact: C): void {
    dispatch(this.beginRoutes, contact, 0);
  }

  /** Routes one end-contact. Mirrors C# `ContactRouter2D`'s explicit `IPhysxContactListener2D.EndContact`. */
  endContact(contact: C): void {
    dispatch(this.endRoutes, contact, 0);
  }

  /** Routes one post-solve with an already-reduced `impulse` (the largest normal impulse). Mirrors C#
   * `ContactRouter2D`'s explicit `IPhysxContactListener2D.PostSolve`. */
  postSolve(contact: C, impulse: number): void {
    dispatch(this.postSolveRoutes, contact, impulse);
  }
}

function dispatch<C extends ContactLike>(routes: readonly Route<C>[], contact: C, impulse: number): void {
  if (routes.length === 0) return;
  const ua = tagOf(contact.getFixtureA());
  const ub = tagOf(contact.getFixtureB());
  if (ua == null || ub == null) return;
  for (const r of routes) r.invoke(contact, ua, ub, null, impulse);
}

function route<TA, TB, C extends ContactLike>(
  isA: TagGuard<TA>,
  isB: TagGuard<TB>,
  fn: (view: RoutedContact2D<TA, TB, C>, impulse: number) => 1 | 2,
): Route<C> {
  const view = new RoutedContact2D<TA, TB, C>();
  return {
    invoke(contact, ua, ub, wm, impulse) {
      if (!tryRoute(contact, isA, isB, view, ua, ub)) return 0;
      if (wm) view.setGeometry(wm);
      else view.pointCount = 0;
      return fn(view, impulse);
    },
  };
}

function maxNormalImpulse(impulse: unknown): number {
  const ni = (impulse as { normalImpulses?: readonly number[] } | undefined)?.normalImpulses;
  if (!ni) return 0;
  let max = 0;
  for (const v of ni) max = Math.max(max, v);
  return max;
}

/**
 * Touching contacts of the pair (isA, isB) in the world's contact list order: touching, enabled,
 * with manifold points; oriented (a = isA's tag, normal a → b), `point` / `normal` filled. Mirrors C#
 * `ContactQueries2D.TouchingContacts<TA, TB>(world)`. Callback-based (C# returns an enumerable);
 * the view passed to `fn` is reused. Use for polling after a step; for event-time dispatch use
 * {@link ContactRouter2D}.
 */
export function touchingContacts<TA, TB, C extends ContactLike & ContactListLike<C>>(
  world: ContactWorldLike<C>,
  isA: TagGuard<TA>,
  isB: TagGuard<TB>,
  fn: (c: RoutedContact2D<TA, TB, C>) => void,
): void {
  const view = new RoutedContact2D<TA, TB, C>();
  for (let c = world.getContactList(); c; c = c.getNext()) {
    if (!c.isTouching() || !c.isEnabled()) continue;
    if (!tryRoute(c, isA, isB, view)) continue;
    const wm = c.getWorldManifold(null);
    if (!wm || wm.pointCount === 0) continue;
    view.setGeometry(wm);
    fn(view);
  }
}

/**
 * Touching contacts of one body with fixtures whose tag passes `isOther`, in the body's contact
 * list order (= the world order restricted to the body), oriented with the other side as `a` and
 * this body as `b`: `normal` points from the other fixture toward this body (out of a surface,
 * toward the body resting on it). Mirrors C# `ContactQueries2D.TouchingContactsOf<TOther>(world, body)`.
 * Note `isOther` is called with the other side's raw tag, which may be null / undefined.
 */
export function touchingContactsOf<TOther, C extends ContactLike>(
  body: ContactEdgeBodyLike<C>,
  isOther: TagGuard<TOther>,
  fn: (c: RoutedContact2D<TOther, unknown, C>) => void,
): void {
  const view = new RoutedContact2D<TOther, unknown, C>();
  for (let e = body.getContactList(); e; e = e.next ?? null) {
    const c = e.contact;
    if (!c.isTouching() || !c.isEnabled()) continue;
    const fa = c.getFixtureA();
    const fb = c.getFixtureB();
    const bodyIsA = (fa.getBody() as unknown) === body;
    const other = tagOf(bodyIsA ? fb : fa);
    if (!isOther(other)) continue;
    const wm = c.getWorldManifold(null);
    if (!wm || wm.pointCount === 0) continue;
    view.contact = c;
    view.a = other;
    view.b = tagOf(bodyIsA ? fa : fb);
    view.swapped = bodyIsA;
    view.setGeometry(wm);
    fn(view);
  }
}
