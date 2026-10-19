/**
 * Physics layer. planck.js (Box2D 2.3) collides chains on both sides with its own edge algorithm;
 * the C# Altruist engine runs Box2DSharp 0.6 (Box2D 2.4), whose chain edges are one-sided and
 * smoothed over their neighbours ("ghost collisions", box2d.org/posts/2020/06/ghost-collisions).
 * Rolling or landing across a chain's vertices then plays differently on the two sides.
 * {@link installBox2D24ChainContacts} replaces planck's chain-vs-polygon and chain-vs-circle
 * contacts with a port of Box2DSharp 0.6's `CollideEdgeAndPolygon` / `CollideEdgeAndCircle`, so a
 * planck world collides chains like the server (a chain collides on the right of its winding: a
 * loop wound clockwise on its inside; collide on both sides with a reversed copy, as C#
 * `PhysxShape2D.Chain(..., twoSided: true)` does).
 */

interface Vec {
  x: number;
  y: number;
}

interface Xf {
  p: Vec;
  q: { s: number; c: number };
}

interface ContactIdLike {
  setFeatures(indexA: number, typeA: number, indexB: number, typeB: number): void;
  set(o: ContactIdLike): void;
  swapFeatures(): void;
}

interface ClipVertexLike {
  v: Vec;
  id: ContactIdLike;
  recycle(): void;
}

interface ManifoldLike {
  type: number;
  pointCount: number;
  localNormal: Vec;
  localPoint: Vec;
  points: { localPoint: Vec; id: ContactIdLike & { key?: number } }[];
}

interface ChainLike {
  m_vertices: Vec[];
  m_count: number;
  m_radius: number;
  m_prevVertex: Vec | null;
  m_hasPrevVertex: boolean;
  m_nextVertex: Vec | null;
  m_hasNextVertex: boolean;
}

interface PolygonLike {
  m_centroid: Vec;
  m_vertices: Vec[];
  m_normals: Vec[];
  m_count: number;
  m_radius: number;
}

interface CircleLike {
  m_p: Vec;
  m_radius: number;
}

interface FixtureLike {
  getShape(): unknown;
}

type ContactFn = (manifold: ManifoldLike, xfA: Xf, fA: FixtureLike, indexA: number, xfB: Xf, fB: FixtureLike, indexB: number) => void;

/**
 * The parts of the planck module this needs (pass `import * as planck from 'planck'`). planck
 * registers its own contacts with the static `Contact.addType`, which its typings leave out.
 */
export interface PlanckContacts {
  Contact: object;
  ChainShape: { TYPE: string };
  PolygonShape: { TYPE: string };
  CircleShape: { TYPE: string };
  ClipVertex: new () => ClipVertexLike;
  clipSegmentToLine(vOut: ClipVertexLike[], vIn: ClipVertexLike[], normal: Vec, offset: number, vertexIndexA: number): number;
  ManifoldType: { e_circles: number; e_faceA: number; e_faceB: number };
  ContactFeatureType: { e_vertex: number; e_face: number };
}

/** A chain's child edge as Box2DSharp builds it: one-sided, an open chain's ends ghosted by themselves. */
interface Edge {
  v0: Vec;
  v1: Vec;
  v2: Vec;
  v3: Vec;
  radius: number;
}

const MAX_FLOAT = 3.4028234663852886e38;
const EPSILON = 1.1920929e-7;

const dot = (a: Vec, b: Vec) => a.x * b.x + a.y * b.y;
const cross = (a: Vec, b: Vec) => a.x * b.y - a.y * b.x;
const sub = (a: Vec, b: Vec): Vec => ({ x: a.x - b.x, y: a.y - b.y });
/** Box2DSharp's Normalize: a vector shorter than epsilon stays as it is. */
function normalized(v: Vec): Vec {
  const len = Math.sqrt(v.x * v.x + v.y * v.y);
  return len < EPSILON ? v : { x: v.x / len, y: v.y / len };
}
const rot = (q: Xf['q'], v: Vec): Vec => ({ x: q.c * v.x - q.s * v.y, y: q.s * v.x + q.c * v.y });
const rotT = (q: Xf['q'], v: Vec): Vec => ({ x: q.c * v.x + q.s * v.y, y: -q.s * v.x + q.c * v.y });
const mul = (xf: Xf, v: Vec): Vec => {
  const r = rot(xf.q, v);
  return { x: r.x + xf.p.x, y: r.y + xf.p.y };
};
const mulT = (xf: Xf, v: Vec): Vec => rotT(xf.q, sub(v, xf.p));
/** B's transform in A's frame (Box2D MulT(xfA, xfB)). */
function relative(a: Xf, b: Xf): Xf {
  const q = { s: a.q.c * b.q.s - a.q.s * b.q.c, c: a.q.c * b.q.c + a.q.s * b.q.s };
  return { q, p: rotT(a.q, sub(b.p, a.p)) };
}

function childEdge(chain: ChainLike, i: number): Edge {
  const v = chain.m_vertices;
  const prev = chain.m_hasPrevVertex && chain.m_prevVertex ? chain.m_prevVertex : v[0]!;
  const next = chain.m_hasNextVertex && chain.m_nextVertex ? chain.m_nextVertex : v[chain.m_count - 1]!;
  return { v0: i > 0 ? v[i - 1]! : prev, v1: v[i]!, v2: v[i + 1]!, v3: i < chain.m_count - 2 ? v[i + 2]! : next, radius: chain.m_radius };
}

/**
 * Makes planck collide chains with polygons and circles as Box2DSharp 0.6 does (see the module
 * comment). Global for the planck module: call once before building worlds.
 */
export function installBox2D24ChainContacts(planck: PlanckContacts): void {
  const contact = planck.Contact as { addType?: (typeA: string, typeB: string, fn: ContactFn) => void };
  if (typeof contact.addType !== 'function') throw new Error('planck: Contact.addType is missing (planck 1.x registers contacts with it)');
  const addType = contact.addType.bind(planck.Contact);
  const { ManifoldType: MT, ContactFeatureType: CF } = planck;
  const clip = [new planck.ClipVertex(), new planck.ClipVertex()];
  const clip1 = [new planck.ClipVertex(), new planck.ClipVertex()];
  const clip2 = [new planck.ClipVertex(), new planck.ClipVertex()];

  const edgeCircle = (m: ManifoldLike, e: Edge, xfA: Xf, circle: CircleLike, xfB: Xf) => {
    m.pointCount = 0;
    const Q = mulT(xfA, mul(xfB, circle.m_p));
    const A = e.v1;
    const B = e.v2;
    const ev = sub(B, A);
    let n: Vec = { x: ev.y, y: -ev.x };
    const offset = dot(n, sub(Q, A));
    if (offset < 0) return;
    const u = dot(ev, sub(B, Q));
    const v = dot(ev, sub(Q, A));
    const radius = e.radius + circle.m_radius;
    const vertexContact = (P: Vec, indexA: number) => {
      m.pointCount = 1;
      m.type = MT.e_circles;
      m.localNormal.x = 0;
      m.localNormal.y = 0;
      m.localPoint.x = P.x;
      m.localPoint.y = P.y;
      const point = m.points[0]!;
      point.localPoint.x = circle.m_p.x;
      point.localPoint.y = circle.m_p.y;
      point.id.setFeatures(indexA, CF.e_vertex, 0, CF.e_vertex);
    };
    if (v <= 0) {
      const d = sub(Q, A);
      if (dot(d, d) > radius * radius) return;
      // Is the circle in region AB of the previous edge?
      if (dot(sub(A, e.v0), sub(A, Q)) > 0) return;
      return vertexContact(A, 0);
    }
    if (u <= 0) {
      const d = sub(Q, B);
      if (dot(d, d) > radius * radius) return;
      // Is the circle in region AB of the next edge?
      if (dot(sub(e.v3, B), sub(Q, B)) > 0) return;
      return vertexContact(B, 1);
    }
    const den = dot(ev, ev);
    const P = { x: (u * A.x + v * B.x) / den, y: (u * A.y + v * B.y) / den };
    const d = sub(Q, P);
    if (dot(d, d) > radius * radius) return;
    if (offset < 0) n = { x: -n.x, y: -n.y };
    n = normalized(n);
    m.pointCount = 1;
    m.type = MT.e_faceA;
    m.localNormal.x = n.x;
    m.localNormal.y = n.y;
    m.localPoint.x = A.x;
    m.localPoint.y = A.y;
    const point = m.points[0]!;
    point.localPoint.x = circle.m_p.x;
    point.localPoint.y = circle.m_p.y;
    point.id.setFeatures(0, CF.e_face, 0, CF.e_vertex);
  };

  const edgePolygon = (m: ManifoldLike, e: Edge, xfA: Xf, poly: PolygonLike, xfB: Xf) => {
    m.pointCount = 0;
    const xf = relative(xfA, xfB);
    const centroidB = mul(xf, poly.m_centroid);
    const v1 = e.v1;
    const v2 = e.v2;
    const edge1 = normalized(sub(v2, v1));
    // Normal points to the right for a CCW winding.
    const normal1 = { x: edge1.y, y: -edge1.x };
    if (dot(normal1, sub(centroidB, v1)) < 0) return;

    const count = poly.m_count;
    const vs: Vec[] = [];
    const ns: Vec[] = [];
    for (let i = 0; i < count; i++) {
      vs.push(mul(xf, poly.m_vertices[i]!));
      ns.push(rot(xf.q, poly.m_normals[i]!));
    }
    const radius = poly.m_radius + e.radius;

    // Edge separation: the edge normal or its opposite, whichever the polygon overlaps least.
    let edgeAxis = { type: 1, index: -1, separation: -MAX_FLOAT, normal: { x: 0, y: 0 } };
    for (const [j, axis] of [normal1, { x: -normal1.x, y: -normal1.y }].entries()) {
      let sj = MAX_FLOAT;
      for (const p of vs) sj = Math.min(sj, dot(axis, sub(p, v1)));
      if (sj > edgeAxis.separation) edgeAxis = { type: 1, index: j, separation: sj, normal: axis };
    }
    if (edgeAxis.separation > radius) return;

    let polygonAxis = { type: 0, index: -1, separation: -MAX_FLOAT, normal: { x: 0, y: 0 } };
    for (let i = 0; i < count; i++) {
      const n = { x: -ns[i]!.x, y: -ns[i]!.y };
      const s = Math.min(dot(n, sub(vs[i]!, v1)), dot(n, sub(vs[i]!, v2)));
      if (s > polygonAxis.separation) polygonAxis = { type: 2, index: i, separation: s, normal: n };
    }
    if (polygonAxis.separation > radius) return;

    // Box2DSharp 0.6 compares a fresh axis (separation 0) here, not polygonAxis as Box2D 2.4.1
    // does; the server runs that, so this does too.
    const relativeTol = 0.98;
    const absoluteTol = 0.001;
    let primary = 0 - radius > relativeTol * (edgeAxis.separation - radius) + absoluteTol ? polygonAxis : edgeAxis;

    // Smooth collision over the neighbouring edges (the Gauss map).
    const edge0 = normalized(sub(v1, e.v0));
    const normal0 = { x: edge0.y, y: -edge0.x };
    const convex1 = cross(edge0, edge1) >= 0;
    const edge2 = normalized(sub(e.v3, v2));
    const normal2 = { x: edge2.y, y: -edge2.x };
    const convex2 = cross(edge1, edge2) >= 0;
    const sinTol = 0.1;
    if (dot(primary.normal, edge1) <= 0) {
      if (convex1) {
        if (cross(primary.normal, normal0) > sinTol) return;
      } else {
        primary = edgeAxis;
      }
    } else if (convex2) {
      if (cross(normal2, primary.normal) > sinTol) return;
    } else {
      primary = edgeAxis;
    }

    let i1: number;
    let i2: number;
    let refV1: Vec;
    let refV2: Vec;
    let refNormal: Vec;
    let side1: Vec;
    let side2: Vec;
    for (const c of [...clip, ...clip1, ...clip2]) c.recycle();
    if (primary.type === 1) {
      m.type = MT.e_faceA;
      // The polygon normal most anti-parallel to the edge normal.
      let best = 0;
      let bestValue = dot(primary.normal, ns[0]!);
      for (let i = 1; i < count; i++) {
        const value = dot(primary.normal, ns[i]!);
        if (value < bestValue) {
          bestValue = value;
          best = i;
        }
      }
      const j1 = best;
      const j2 = j1 + 1 < count ? j1 + 1 : 0;
      setClip(clip[0]!, vs[j1]!, 0, CF.e_face, j1, CF.e_vertex);
      setClip(clip[1]!, vs[j2]!, 0, CF.e_face, j2, CF.e_vertex);
      i1 = 0;
      i2 = 1;
      refV1 = v1;
      refV2 = v2;
      refNormal = primary.normal;
      side1 = { x: -edge1.x, y: -edge1.y };
      side2 = edge1;
    } else {
      m.type = MT.e_faceB;
      setClip(clip[0]!, v2, 1, CF.e_vertex, primary.index, CF.e_face);
      setClip(clip[1]!, v1, 0, CF.e_vertex, primary.index, CF.e_face);
      i1 = primary.index;
      i2 = i1 + 1 < count ? i1 + 1 : 0;
      refV1 = vs[i1]!;
      refV2 = vs[i2]!;
      refNormal = ns[i1]!;
      side1 = { x: refNormal.y, y: -refNormal.x };
      side2 = { x: -side1.x, y: -side1.y };
    }
    if (planck.clipSegmentToLine(clip1, clip, side1, dot(side1, refV1), i1) < 2) return;
    if (planck.clipSegmentToLine(clip2, clip1, side2, dot(side2, refV2), i2) < 2) return;

    const localNormal = primary.type === 1 ? refNormal : poly.m_normals[i1]!;
    const localPoint = primary.type === 1 ? refV1 : poly.m_vertices[i1]!;
    m.localNormal.x = localNormal.x;
    m.localNormal.y = localNormal.y;
    m.localPoint.x = localPoint.x;
    m.localPoint.y = localPoint.y;
    let pointCount = 0;
    for (let i = 0; i < 2; i++) {
      const c = clip2[i]!;
      if (dot(refNormal, sub(c.v, refV1)) > radius) continue;
      const cp = m.points[pointCount]!;
      const at = primary.type === 1 ? mulT(xf, c.v) : c.v;
      cp.localPoint.x = at.x;
      cp.localPoint.y = at.y;
      cp.id.set(c.id);
      if (primary.type !== 1) cp.id.swapFeatures();
      pointCount++;
    }
    m.pointCount = pointCount;
  };

  function setClip(c: ClipVertexLike, v: Vec, indexA: number, typeA: number, indexB: number, typeB: number): void {
    c.v.x = v.x;
    c.v.y = v.y;
    c.id.setFeatures(indexA, typeA, indexB, typeB);
  }

  addType(planck.ChainShape.TYPE, planck.PolygonShape.TYPE, (m, xfA, fA, indexA, xfB, fB) =>
    edgePolygon(m, childEdge(fA.getShape() as ChainLike, indexA), xfA, fB.getShape() as PolygonLike, xfB),
  );
  addType(planck.ChainShape.TYPE, planck.CircleShape.TYPE, (m, xfA, fA, indexA, xfB, fB) =>
    edgeCircle(m, childEdge(fA.getShape() as ChainLike, indexA), xfA, fB.getShape() as CircleLike, xfB),
  );
}
