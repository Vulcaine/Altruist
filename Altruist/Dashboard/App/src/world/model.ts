/** The world viewer's data: the server's DTOs and the pure state they fold into. */

export interface Vec3 {
  readonly x: number;
  readonly y: number;
  readonly z: number;
}

export interface Quat {
  readonly x: number;
  readonly y: number;
  readonly z: number;
  readonly w: number;
}

export interface TransformDto {
  readonly position: Vec3;
  readonly size: Vec3;
  readonly scale: Vec3;
  readonly rotation?: Quat | null;
}

export interface HeightfieldDto {
  readonly width: number;
  readonly height: number;
  readonly cellSizeX: number;
  readonly cellSizeZ: number;
  readonly heightScale: number;
  readonly heights: readonly (readonly number[])[];
}

export interface ColliderDto {
  readonly id: string;
  /** sphere | box | capsule | heightfield (3D); circle | box2d | capsule2d | polygon | chain | edge (2D). */
  readonly shape: string;
  readonly transform: TransformDto;
  readonly isTrigger: boolean;
  /** "world" (3D runtime colliders), "local" (relative to the object: descriptors and 2D body colliders). */
  readonly transformSpace: string;
  readonly heightfield?: HeightfieldDto | null;
  readonly vertices?: readonly { x: number; y: number }[] | null;
}

export interface WorldObject {
  readonly instanceId: string;
  readonly archetype: string;
  readonly name: string;
  readonly zoneId: string;
  readonly clientId: string;
  readonly expired: boolean;
  readonly transform: TransformDto;
  readonly colliders: readonly ColliderDto[];
}

export interface Pose {
  readonly instanceId: string;
  readonly archetype: string;
  readonly name: string;
  readonly position: Vec3;
  readonly rotation: Quat;
}

export interface Gizmo {
  readonly id: string;
  readonly worldIndex: number;
  readonly category: string;
  readonly source: string;
  /** sphere | circle | box | rect | line | polyline | polygon | point | label … */
  readonly type: string;
  readonly label: string;
  /** #RRGGBB or #RRGGBBAA. */
  readonly color: string;
  readonly position: Vec3;
  readonly radius: number;
  readonly width: number;
  readonly height: number;
  readonly points: readonly Vec3[];
  readonly attachToInstanceId: string;
}

export interface WorldSummary {
  readonly index: number;
  readonly name: string;
  readonly dimensions: 2 | 3;
  readonly partitionCount: number;
  readonly objectCount: number;
}

/** A line of the live stream: the whole world (reset) or what changed. */
export interface WorldDelta {
  readonly reset: boolean;
  readonly worldIndex: number;
  readonly timestampUtc: string;
  readonly added: readonly WorldObject[];
  readonly moved: readonly Pose[];
  readonly removed: readonly string[];
  readonly gizmos: readonly Gizmo[];
  readonly removedGizmoIds: readonly string[];
}

export interface WorldState {
  readonly objects: ReadonlyMap<string, WorldObject>;
  readonly gizmos: ReadonlyMap<string, Gizmo>;
  /** Ids changed by the last delta (for the scene to redraw only those). */
  readonly changed: ReadonlySet<string>;
  /** Whether the last delta replaced everything. */
  readonly reset: boolean;
  readonly updatedAt: string | null;
}

export const EMPTY_WORLD: WorldState = { objects: new Map(), gizmos: new Map(), changed: new Set(), reset: true, updatedAt: null };

/** A moved object keeps its colliders; world-space colliders move with it. */
function moveObject(object: WorldObject, pose: Pose): WorldObject {
  const dx = pose.position.x - object.transform.position.x;
  const dy = pose.position.y - object.transform.position.y;
  const dz = pose.position.z - object.transform.position.z;
  return {
    ...object,
    archetype: pose.archetype,
    name: pose.name,
    transform: { ...object.transform, position: pose.position, rotation: pose.rotation },
    colliders: object.colliders.map((c) =>
      c.transformSpace === 'world' && c.shape !== 'heightfield'
        ? { ...c, transform: { ...c.transform, position: { x: c.transform.position.x + dx, y: c.transform.position.y + dy, z: c.transform.position.z + dz }, rotation: pose.rotation } }
        : c,
    ),
  };
}

/** Folds a live-stream line into the state. Pure. */
export function applyDelta(state: WorldState, delta: WorldDelta): WorldState {
  const objects = new Map(delta.reset ? [] : state.objects);
  const gizmos = new Map(delta.reset ? [] : state.gizmos);
  const changed = new Set<string>();
  for (const o of delta.added) {
    objects.set(o.instanceId, o);
    changed.add(o.instanceId);
  }
  for (const pose of delta.moved) {
    const current = objects.get(pose.instanceId);
    if (current) {
      objects.set(pose.instanceId, moveObject(current, pose));
      changed.add(pose.instanceId);
    }
  }
  for (const id of delta.removed) {
    objects.delete(id);
    changed.add(id);
  }
  for (const g of delta.gizmos) gizmos.set(g.id, g);
  for (const id of delta.removedGizmoIds) gizmos.delete(id);
  return { objects, gizmos, changed, reset: delta.reset, updatedAt: delta.timestampUtc };
}

/** Archetypes and how many objects each has, most common first. */
export function archetypeCounts(objects: Iterable<WorldObject>): [string, number][] {
  const counts = new Map<string, number>();
  for (const o of objects) counts.set(o.archetype || '(none)', (counts.get(o.archetype || '(none)') ?? 0) + 1);
  return [...counts.entries()].sort((a, b) => b[1] - a[1] || a[0].localeCompare(b[0]));
}

/** Rotation about z (2D worlds), in radians. */
export function angleOf(q: Quat | null | undefined): number {
  return q ? 2 * Math.atan2(q.z, q.w) : 0;
}
