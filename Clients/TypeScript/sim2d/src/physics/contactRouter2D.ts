/**
 * Physics layer. Contacts dispatched by the kinds of their two tags — mirror of C#
 * `ContactRouter2D`, `RoutedContact2D<TA, TB>` and `ContactQueries2D`. TypeScript has no runtime
 * types for tags, so a pair is given by two type guards (`isSurface`, `isBall`, ...) instead of two
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

export interface ContactFixtureLike<B = ContactBodyLike> {
  getUserData(): unknown;
  getBody(): B;
}

export interface ContactBodyLike {
  getUserData(): unknown;
}

export interface WorldManifoldLike {
  normal: Vec2Like;
  points: readonly Vec2Like[];
  pointCount: number;
}

export interface ContactLike<F extends ContactFixtureLike = ContactFixtureLike> {
  getFixtureA(): F;
  getFixtureB(): F;
  isTouching(): boolean;
  isEnabled(): boolean;
  setEnabled(flag: boolean): void;
  setRestitution(restitution: number): void;
  setFriction(friction: number): void;
  getWorldManifold(out: null): WorldManifoldLike | undefined | null;
}

export interface ContactListLike<C> {
  getNext(): C | null;
}

/** A world whose contacts can be walked (planck `World.getContactList()` / `Contact.getNext()`). */
export interface ContactWorldLike<C extends ContactLike & ContactListLike<C>> {
  getContactList(): C | null;
}

/** A world that emits pre-solve (planck `world.on('pre-solve', fn)`). */
export interface PreSolveWorldLike<C> {
  on(event: 'pre-solve', listener: (contact: C) => void): unknown;
}

/** A body whose contact edges can be walked (planck `Body.getContactList()`). */
export interface ContactEdgeBodyLike<C> {
  getContactList(): ContactEdgeLike<C> | null;
}

export interface ContactEdgeLike<C> {
  contact: C;
  next: ContactEdgeLike<C> | null | undefined;
}

export type TagGuard<T> = (tag: unknown) => tag is T;

/**
 * A contact seen as a typed pair. One view object is reused per router / query: valid only inside
 * the callback that received it (copy what you keep).
 */
export class RoutedContact2D<TA, TB, C extends ContactLike = ContactLike> {
  contact!: C;
  a!: TA;
  b!: TB;
  /** True when the engine's fixture A carries `b`. */
  swapped = false;
  /** Manifold point count (0 when not computed: begin/end queries never compute it). */
  pointCount = 0;
  /** Average of the manifold points. */
  readonly point = { x: 0, y: 0 };
  /** From a's side to b's side. */
  readonly normal = { x: 0, y: 0 };

  get fixtureA(): ReturnType<C['getFixtureA']> {
    return (this.swapped ? this.contact.getFixtureB() : this.contact.getFixtureA()) as ReturnType<C['getFixtureA']>;
  }

  get fixtureB(): ReturnType<C['getFixtureA']> {
    return (this.swapped ? this.contact.getFixtureA() : this.contact.getFixtureB()) as ReturnType<C['getFixtureA']>;
  }

  get bodyA(): ReturnType<ReturnType<C['getFixtureA']>['getBody']> {
    return this.fixtureA.getBody() as ReturnType<ReturnType<C['getFixtureA']>['getBody']>;
  }

  get bodyB(): ReturnType<ReturnType<C['getFixtureA']>['getBody']> {
    return this.fixtureB.getBody() as ReturnType<ReturnType<C['getFixtureA']>['getBody']>;
  }

  /** Disables the contact for this step (pre-solve). */
  disable(): void {
    this.contact.setEnabled(false);
  }

  setRestitution(restitution: number): void {
    this.contact.setRestitution(restitution);
  }

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

/** The tag of a fixture: its user data, else its body's. */
export function tagOf(fixture: ContactFixtureLike): unknown {
  return fixture.getUserData() ?? fixture.getBody().getUserData();
}

/** Orients a contact for the pair (isA, isB) into `view`; false when it does not match. */
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

export class ContactRouter2D<C extends ContactLike = ContactLike> {
  private readonly filters: Route<C>[] = [];
  private readonly preSolveRoutes: Route<C>[] = [];
  private readonly beginRoutes: Route<C>[] = [];
  private readonly endRoutes: Route<C>[] = [];
  private readonly postSolveRoutes: Route<C>[] = [];

  /** Subscribes to a planck world's contact events (`pre-solve`, `begin-contact`, `end-contact`, `post-solve`). */
  attach(world: { on(event: string, listener: (contact: C, extra?: unknown) => void): unknown }): this {
    world.on('pre-solve', (c) => this.preSolve(c));
    world.on('begin-contact', (c) => this.beginContact(c));
    world.on('end-contact', (c) => this.endContact(c));
    world.on('post-solve', (c, impulse) => this.postSolve(c, maxNormalImpulse(impulse)));
    return this;
  }

  /** A pre-solve filter for the pair: false disables the contact and skips the rest of its routing. */
  filterPreSolve<TA, TB>(isA: TagGuard<TA>, isB: TagGuard<TB>, keep: (c: RoutedContact2D<TA, TB, C>) => boolean): this {
    this.filters.push(route(isA, isB, (v) => (keep(v) ? 1 : 2)));
    return this;
  }

  /** Pre-solve handler for the pair (after the filters, only with manifold points). */
  onPreSolve<TA, TB>(isA: TagGuard<TA>, isB: TagGuard<TB>, handler: (c: RoutedContact2D<TA, TB, C>) => void): this {
    this.preSolveRoutes.push(route(isA, isB, (v) => (handler(v), 1)));
    return this;
  }

  onBegin<TA, TB>(isA: TagGuard<TA>, isB: TagGuard<TB>, handler: (c: RoutedContact2D<TA, TB, C>) => void): this {
    this.beginRoutes.push(route(isA, isB, (v) => (handler(v), 1)));
    return this;
  }

  onEnd<TA, TB>(isA: TagGuard<TA>, isB: TagGuard<TB>, handler: (c: RoutedContact2D<TA, TB, C>) => void): this {
    this.endRoutes.push(route(isA, isB, (v) => (handler(v), 1)));
    return this;
  }

  /** Post-solve handler with the largest normal impulse. */
  onPostSolve<TA, TB>(isA: TagGuard<TA>, isB: TagGuard<TB>, handler: (c: RoutedContact2D<TA, TB, C>, impulse: number) => void): this {
    this.postSolveRoutes.push(route(isA, isB, (v, impulse) => (handler(v, impulse), 1)));
    return this;
  }

  /** Routes one pre-solve. */
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

  beginContact(contact: C): void {
    dispatch(this.beginRoutes, contact, 0);
  }

  endContact(contact: C): void {
    dispatch(this.endRoutes, contact, 0);
  }

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
 * with manifold points; oriented (a = isA's tag, normal a → b). C#: `world.TouchingContacts<TA, TB>()`.
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
 * toward the car on it). C#: `world.TouchingContactsOf<TOther>(body)`.
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
