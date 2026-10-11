import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { angleOf, type ColliderDto, type Gizmo, type HeightfieldDto, type Vec3, type WorldObject, type WorldState } from './model';

/**
 * The world viewer's three.js scene. 3D worlds are drawn as they are (y up) under an orbiting perspective camera; 2D
 * worlds lie on the ground plane, (x, y) → (x, 0, −y), under a top-down orthographic camera, so a 2D rotation about z
 * is a rotation about the scene's y axis. Frames are drawn only when something changed or the camera moves.
 */
export class WorldScene {
  private readonly renderer: THREE.WebGLRenderer;
  private readonly scene = new THREE.Scene();
  private readonly objectRoot = new THREE.Group();
  private readonly gizmoRoot = new THREE.Group();
  private readonly objectNodes = new Map<string, THREE.Group>();
  private readonly gizmoNodes = new Map<string, THREE.Object3D>();
  private readonly raycaster = new THREE.Raycaster();
  private camera: THREE.PerspectiveCamera | THREE.OrthographicCamera;
  private controls: OrbitControls;
  private grid: THREE.GridHelper | null = null;
  private selection: THREE.BoxHelper | null = null;
  private selectedId: string | null = null;
  private dimensions: 2 | 3 = 3;
  private hiddenArchetypes: ReadonlySet<string> = new Set();
  private hiddenGizmoCategories: ReadonlySet<string> = new Set();
  private frame = 0;
  private readonly resizeObserver: ResizeObserver;
  /** Frames drawn per second, for the toolbar. */
  fps = 0;
  private frames = 0;
  private fpsSince = performance.now();

  constructor(private readonly container: HTMLElement) {
    this.renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
    container.appendChild(this.renderer.domElement);
    this.scene.add(this.objectRoot, this.gizmoRoot);
    this.scene.add(new THREE.HemisphereLight(0xdfe8ff, 0x1a1f26, 1.1));
    const sun = new THREE.DirectionalLight(0xffffff, 1.2);
    sun.position.set(40, 80, 30);
    this.scene.add(sun);
    this.camera = new THREE.PerspectiveCamera(55, 1, 0.1, 50000);
    this.controls = this.makeControls();
    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(container);
    this.resize();
  }

  /** 2D or 3D: swaps the camera (top-down orthographic for 2D). */
  setDimensions(dimensions: 2 | 3): void {
    if (dimensions === this.dimensions && this.controls) return;
    this.dimensions = dimensions;
    this.controls.dispose();
    this.camera = dimensions === 2 ? new THREE.OrthographicCamera(-50, 50, 50, -50, -10000, 10000) : new THREE.PerspectiveCamera(55, 1, 0.1, 50000);
    if (dimensions === 2) {
      this.camera.position.set(0, 1000, 0);
      this.camera.up.set(0, 0, -1);
    }
    this.controls = this.makeControls();
    this.resize();
  }

  private makeControls(): OrbitControls {
    const controls = new OrbitControls(this.camera, this.renderer.domElement);
    controls.enableDamping = false;
    controls.screenSpacePanning = true;
    if (this.dimensions === 2) {
      controls.enableRotate = false;
      controls.mouseButtons = { LEFT: THREE.MOUSE.PAN, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.PAN };
    }
    controls.addEventListener('change', () => this.invalidate());
    return controls;
  }

  /** A world point in scene coordinates. */
  toScene(v: Vec3): THREE.Vector3 {
    return this.dimensions === 2 ? new THREE.Vector3(v.x, 0, -v.y) : new THREE.Vector3(v.x, v.y, v.z);
  }

  /** Draws the state: everything after a reset, otherwise only the changed objects. */
  setWorld(state: WorldState): void {
    if (state.reset) {
      for (const id of [...this.objectNodes.keys()]) this.removeNode(id);
      for (const o of state.objects.values()) this.upsertNode(o);
    } else {
      for (const id of state.changed) {
        const o = state.objects.get(id);
        if (o) this.upsertNode(o);
        else this.removeNode(id);
      }
    }
    this.syncGizmos(state.gizmos);
    this.updateGrid();
    this.refreshSelection();
    this.invalidate();
  }

  setHidden(archetypes: ReadonlySet<string>, gizmoCategories: ReadonlySet<string>): void {
    this.hiddenArchetypes = archetypes;
    this.hiddenGizmoCategories = gizmoCategories;
    for (const node of this.objectNodes.values()) node.visible = !archetypes.has(node.userData.archetype as string);
    for (const node of this.gizmoNodes.values()) node.visible = !gizmoCategories.has(node.userData.category as string);
    this.invalidate();
  }

  select(id: string | null): void {
    this.selectedId = id;
    this.refreshSelection();
    this.invalidate();
  }

  /** Points the camera at an object (or at everything with null). */
  focus(id: string | null): void {
    const target = id ? this.objectNodes.get(id) : this.objectRoot;
    if (!target) return;
    const box = new THREE.Box3().setFromObject(target);
    if (box.isEmpty()) return;
    const center = box.getCenter(new THREE.Vector3());
    const radius = Math.max(box.getSize(new THREE.Vector3()).length() / 2, 2);
    this.controls.target.copy(center);
    if (this.camera instanceof THREE.OrthographicCamera) {
      this.camera.position.set(center.x, center.y + 1000, center.z);
      this.fitOrtho(radius);
    } else {
      // The whole world from a three-quarter view above; one object from where the camera already looks.
      const dir = id ? this.camera.position.clone().sub(center).normalize() : new THREE.Vector3(0.55, 0.75, 0.9).normalize();
      if (dir.lengthSq() < 0.5) dir.set(0.55, 0.75, 0.9).normalize();
      this.camera.position.copy(center).addScaledVector(dir, radius * 2.6);
      this.camera.far = Math.max(50000, radius * 20);
      this.camera.updateProjectionMatrix();
    }
    this.controls.update();
    this.invalidate();
  }

  private fitOrtho(radius: number): void {
    const camera = this.camera as THREE.OrthographicCamera;
    const aspect = this.container.clientWidth / Math.max(1, this.container.clientHeight);
    const half = radius * 1.2;
    camera.left = -half * aspect;
    camera.right = half * aspect;
    camera.top = half;
    camera.bottom = -half;
    camera.zoom = 1;
    camera.updateProjectionMatrix();
  }

  /** The object under a screen point, or null. */
  pick(clientX: number, clientY: number): string | null {
    const rect = this.renderer.domElement.getBoundingClientRect();
    const pointer = new THREE.Vector2(((clientX - rect.left) / rect.width) * 2 - 1, -((clientY - rect.top) / rect.height) * 2 + 1);
    this.raycaster.setFromCamera(pointer, this.camera);
    const visible = [...this.objectNodes.values()].filter((n) => n.visible);
    for (const hit of this.raycaster.intersectObjects(visible, true)) {
      let node: THREE.Object3D | null = hit.object;
      while (node && !node.userData.instanceId) node = node.parent;
      if (node && !node.userData.terrain) return node.userData.instanceId as string;
    }
    return null;
  }

  get canvas(): HTMLCanvasElement {
    return this.renderer.domElement;
  }

  dispose(): void {
    cancelAnimationFrame(this.frame);
    this.resizeObserver.disconnect();
    this.controls.dispose();
    for (const id of [...this.objectNodes.keys()]) this.removeNode(id);
    for (const node of this.gizmoNodes.values()) dispose(node);
    this.renderer.dispose();
    this.renderer.domElement.remove();
  }

  /** Asks for one frame. */
  invalidate(): void {
    if (this.frame) return;
    this.frame = requestAnimationFrame(() => {
      this.frame = 0;
      this.renderer.render(this.scene, this.camera);
      this.frames++;
      const now = performance.now();
      if (now - this.fpsSince >= 1000) {
        this.fps = Math.round((this.frames * 1000) / (now - this.fpsSince));
        this.frames = 0;
        this.fpsSince = now;
      }
    });
  }

  private resize(): void {
    const w = Math.max(1, this.container.clientWidth);
    const h = Math.max(1, this.container.clientHeight);
    this.renderer.setSize(w, h, false);
    this.renderer.domElement.style.width = '100%';
    this.renderer.domElement.style.height = '100%';
    if (this.camera instanceof THREE.PerspectiveCamera) {
      this.camera.aspect = w / h;
      this.camera.updateProjectionMatrix();
    } else {
      const halfHeight = (this.camera.top - this.camera.bottom) / 2;
      this.camera.left = -halfHeight * (w / h);
      this.camera.right = halfHeight * (w / h);
      this.camera.updateProjectionMatrix();
    }
    this.invalidate();
  }

  private upsertNode(o: WorldObject): void {
    this.removeNode(o.instanceId);
    const node = this.dimensions === 2 ? buildObject2D(o, this) : buildObject3D(o);
    node.userData = { instanceId: o.instanceId, archetype: o.archetype || '(none)', terrain: o.colliders.some((c) => c.shape === 'heightfield') };
    node.visible = !this.hiddenArchetypes.has(node.userData.archetype as string);
    this.objectNodes.set(o.instanceId, node);
    this.objectRoot.add(node);
  }

  private removeNode(id: string): void {
    const node = this.objectNodes.get(id);
    if (!node) return;
    this.objectRoot.remove(node);
    dispose(node);
    this.objectNodes.delete(id);
  }

  private syncGizmos(gizmos: ReadonlyMap<string, Gizmo>): void {
    for (const [id, node] of this.gizmoNodes) {
      if (!gizmos.has(id) || node.userData.version !== gizmos.get(id)) {
        this.gizmoRoot.remove(node);
        dispose(node);
        this.gizmoNodes.delete(id);
      }
    }
    for (const [id, g] of gizmos) {
      if (this.gizmoNodes.has(id)) continue;
      const node = buildGizmo(g, (v) => this.toScene(v), this.dimensions);
      node.userData = { category: g.category || 'other', version: g };
      node.visible = !this.hiddenGizmoCategories.has(g.category || 'other');
      this.gizmoNodes.set(id, node);
      this.gizmoRoot.add(node);
    }
  }

  private updateGrid(): void {
    const box = new THREE.Box3().setFromObject(this.objectRoot);
    const size = box.isEmpty() ? 100 : Math.max(20, Math.ceil(Math.max(box.max.x - box.min.x, box.max.z - box.min.z) * 1.2));
    if (this.grid && this.grid.userData.size === size) return;
    if (this.grid) {
      this.scene.remove(this.grid);
      dispose(this.grid);
    }
    const divisions = Math.min(200, Math.max(10, Math.round(size / 10)));
    this.grid = new THREE.GridHelper(size, divisions, 0x3b4655, 0x232a33);
    this.grid.userData.size = size;
    if (!box.isEmpty()) this.grid.position.set((box.min.x + box.max.x) / 2, this.dimensions === 2 ? -0.05 : box.min.y, (box.min.z + box.max.z) / 2);
    this.scene.add(this.grid);
  }

  private refreshSelection(): void {
    if (this.selection) {
      this.scene.remove(this.selection);
      this.selection.dispose();
      this.selection = null;
    }
    const node = this.selectedId ? this.objectNodes.get(this.selectedId) : null;
    if (!node) return;
    this.selection = new THREE.BoxHelper(node, 0xfacc15);
    this.scene.add(this.selection);
  }
}

// ---------------------------------------------------------------- objects

const PALETTE_CACHE = new Map<string, THREE.Color>();

/** A stable colour per archetype. */
export function archetypeColor(archetype: string): THREE.Color {
  let color = PALETTE_CACHE.get(archetype);
  if (!color) {
    let hash = 0;
    for (const ch of archetype) hash = (hash * 31 + ch.charCodeAt(0)) | 0;
    color = new THREE.Color().setHSL(((hash >>> 0) % 360) / 360, 0.62, 0.58);
    PALETTE_CACHE.set(archetype, color);
  }
  return color;
}

function materials(archetype: string, trigger: boolean) {
  const color = archetypeColor(archetype || '(none)');
  return {
    fill: new THREE.MeshStandardMaterial({ color, emissive: color, emissiveIntensity: 0.15, transparent: true, opacity: trigger ? 0.15 : 0.35, depthWrite: !trigger }),
    line: new THREE.LineBasicMaterial({ color, transparent: true, opacity: trigger ? 0.5 : 0.95 }),
  };
}

function withEdges(geometry: THREE.BufferGeometry, archetype: string, trigger: boolean): THREE.Mesh {
  const m = materials(archetype, trigger);
  const mesh = new THREE.Mesh(geometry, m.fill);
  mesh.add(new THREE.LineSegments(new THREE.EdgesGeometry(geometry, 25), m.line));
  return mesh;
}

const q = (r: { x: number; y: number; z: number; w: number } | null | undefined) => (r ? new THREE.Quaternion(r.x, r.y, r.z, r.w) : new THREE.Quaternion());
const abs = (v: number, min: number) => Math.max(min, Math.abs(v));

function buildObject3D(o: WorldObject): THREE.Group {
  const node = new THREE.Group();
  const p = o.transform.position;
  node.position.set(p.x, p.y, p.z);
  const objectRotation = q(o.transform.rotation);
  if (o.colliders.length === 0) {
    const s = o.transform.size;
    const marker = withEdges(new THREE.BoxGeometry(abs(s.x, 0.5), abs(s.y, 0.5), abs(s.z, 0.5)), o.archetype, false);
    marker.quaternion.copy(objectRotation);
    node.add(marker);
    return node;
  }
  for (const c of o.colliders) {
    const mesh = c.shape === 'heightfield' ? buildHeightfield(c.heightfield) : buildCollider3D(c, o.archetype);
    const cp = c.transform.position;
    if (c.transformSpace === 'world' || c.shape === 'heightfield') {
      mesh.position.set(cp.x - p.x, cp.y - p.y, cp.z - p.z);
      if (c.shape !== 'heightfield') mesh.quaternion.copy(q(c.transform.rotation));
    } else {
      mesh.position.copy(new THREE.Vector3(cp.x, cp.y, cp.z).applyQuaternion(objectRotation));
      mesh.quaternion.copy(objectRotation).multiply(q(c.transform.rotation));
    }
    node.add(mesh);
  }
  return node;
}

function buildCollider3D(c: ColliderDto, archetype: string): THREE.Mesh {
  const s = c.transform.size;
  const k = c.transform.scale;
  switch (c.shape) {
    case 'sphere':
      return withEdges(new THREE.SphereGeometry(abs(s.x * k.x, 0.05), 18, 12), archetype, c.isTrigger);
    case 'capsule':
      return withEdges(new THREE.CapsuleGeometry(abs(s.x * k.x, 0.05), abs(s.y * k.y * 2, 0.05), 6, 12), archetype, c.isTrigger);
    default:
      // Box sizes are half extents.
      return withEdges(new THREE.BoxGeometry(abs(s.x * k.x * 2, 0.05), abs(s.y * k.y * 2, 0.05), abs(s.z * k.z * 2, 0.05)), archetype, c.isTrigger);
  }
}

function buildHeightfield(hf: HeightfieldDto | null | undefined): THREE.Mesh {
  const geometry = new THREE.BufferGeometry();
  if (!hf || hf.width < 2 || hf.height < 2) return new THREE.Mesh(geometry);
  const positions = new Float32Array(hf.width * hf.height * 3);
  for (let z = 0; z < hf.height; z++)
    for (let x = 0; x < hf.width; x++) {
      const i = (z * hf.width + x) * 3;
      positions[i] = x * hf.cellSizeX;
      // Heights are already in world units (heightScale is what the server applied).
      positions[i + 1] = hf.heights[x]?.[z] ?? 0;
      positions[i + 2] = z * hf.cellSizeZ;
    }
  const indices: number[] = [];
  for (let z = 0; z < hf.height - 1; z++)
    for (let x = 0; x < hf.width - 1; x++) {
      const a = z * hf.width + x;
      indices.push(a, a + hf.width, a + 1, a + 1, a + hf.width, a + hf.width + 1);
    }
  geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
  geometry.setIndex(indices);
  geometry.computeVertexNormals();
  const mesh = new THREE.Mesh(geometry, new THREE.MeshStandardMaterial({ color: 0x3a6f8f, transparent: true, opacity: 0.55, side: THREE.DoubleSide }));
  mesh.add(new THREE.LineSegments(new THREE.WireframeGeometry(geometry), new THREE.LineBasicMaterial({ color: 0x38bdf8, transparent: true, opacity: 0.18 })));
  return mesh;
}

/** A flat 2D shape (in its own xy plane) laid on the ground: (x, y) → (x, 0, −y). */
function flat(shape: THREE.Shape | THREE.Vector2[], archetype: string, trigger: boolean, closed = true): THREE.Object3D {
  const m = materials(archetype, trigger);
  const group = new THREE.Group();
  group.rotation.x = -Math.PI / 2;
  if (shape instanceof THREE.Shape) {
    const geometry = new THREE.ShapeGeometry(shape, 24);
    group.add(new THREE.Mesh(geometry, m.fill));
    const outline = shape.getPoints(32);
    group.add(new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(outline), m.line));
  } else {
    const geometry = new THREE.BufferGeometry().setFromPoints(shape);
    group.add(closed ? new THREE.LineLoop(geometry, m.line) : new THREE.Line(geometry, m.line));
  }
  return group;
}

function shape2D(c: ColliderDto): THREE.Shape | THREE.Vector2[] {
  const s = c.transform.size;
  switch (c.shape) {
    case 'circle': {
      const shape = new THREE.Shape();
      shape.absarc(0, 0, abs(s.x, 0.02), 0, Math.PI * 2, false);
      return shape;
    }
    case 'capsule2d': {
      const r = abs(s.x, 0.02);
      const half = Math.abs(s.y);
      const shape = new THREE.Shape();
      shape.absarc(half, 0, r, -Math.PI / 2, Math.PI / 2, false);
      shape.absarc(-half, 0, r, Math.PI / 2, (Math.PI * 3) / 2, false);
      return shape;
    }
    case 'polygon':
      return new THREE.Shape((c.vertices ?? []).map((v) => new THREE.Vector2(v.x, v.y)));
    case 'chain':
    case 'edge':
      return (c.vertices ?? []).map((v) => new THREE.Vector2(v.x, v.y));
    default: {
      // box2d: half extents.
      const w = abs(s.x, 0.02);
      const h = abs(s.y, 0.02);
      return new THREE.Shape([new THREE.Vector2(-w, -h), new THREE.Vector2(w, -h), new THREE.Vector2(w, h), new THREE.Vector2(-w, h)]);
    }
  }
}

function buildObject2D(o: WorldObject, scene: WorldScene): THREE.Group {
  const node = new THREE.Group();
  node.position.copy(scene.toScene(o.transform.position));
  node.rotation.y = angleOf(o.transform.rotation);
  if (o.colliders.length === 0) {
    const s = o.transform.size;
    const w = abs(s.x / 2, 0.25);
    const h = abs(s.y / 2, 0.25);
    node.add(flat(new THREE.Shape([new THREE.Vector2(-w, -h), new THREE.Vector2(w, -h), new THREE.Vector2(w, h), new THREE.Vector2(-w, h)]), o.archetype, false));
    return node;
  }
  for (const c of o.colliders) {
    const part = new THREE.Group();
    part.position.set(c.transform.position.x, 0, -c.transform.position.y);
    part.rotation.y = angleOf(c.transform.rotation);
    part.add(flat(shape2D(c), o.archetype, c.isTrigger, c.shape !== 'chain' && c.shape !== 'edge'));
    node.add(part);
  }
  // Heading: a short line along the body's +x.
  const heading = new THREE.Line(new THREE.BufferGeometry().setFromPoints([new THREE.Vector3(0, 0.01, 0), new THREE.Vector3(1, 0.01, 0)]), new THREE.LineBasicMaterial({ color: 0xffffff, transparent: true, opacity: 0.5 }));
  node.add(heading);
  return node;
}

// ---------------------------------------------------------------- gizmos

function colorOf(hex: string): { color: THREE.Color; alpha: number } {
  const clean = /^#?[0-9a-f]{6}([0-9a-f]{2})?$/i.test(hex) ? hex.replace('#', '') : '38BDF8FF';
  return { color: new THREE.Color(`#${clean.slice(0, 6)}`), alpha: clean.length === 8 ? parseInt(clean.slice(6), 16) / 255 : 1 };
}

function buildGizmo(g: Gizmo, toScene: (v: Vec3) => THREE.Vector3, dimensions: 2 | 3): THREE.Object3D {
  const { color, alpha } = colorOf(g.color);
  const line = new THREE.LineBasicMaterial({ color, transparent: alpha < 1, opacity: alpha, depthTest: dimensions === 3 });
  const at = toScene(g.position);
  const points = g.points.map(toScene);
  const lineOf = (pts: THREE.Vector3[], closed: boolean) => (closed ? new THREE.LineLoop(new THREE.BufferGeometry().setFromPoints(pts), line) : new THREE.Line(new THREE.BufferGeometry().setFromPoints(pts), line));
  const type = (g.type || 'sphere').toLowerCase();

  if (type === 'polyline' && points.length >= 2) return lineOf(points, false);
  if ((type === 'polygon' || type === 'cone') && points.length >= 3) return lineOf(points, true);
  if (type === 'circle') {
    const r = Math.max(0.01, g.radius || 1);
    const pts = Array.from({ length: 65 }, (_, i) => new THREE.Vector3(at.x + Math.cos((i / 64) * Math.PI * 2) * r, at.y + 0.02, at.z + Math.sin((i / 64) * Math.PI * 2) * r));
    return lineOf(pts, false);
  }
  if (type === 'rect' || type === 'solid-rect' || type === 'filled-rect') {
    const w = g.width || 1;
    const h = g.height || 1;
    const d = dimensions === 2 ? -h : h;
    const corners = [new THREE.Vector3(at.x, at.y + 0.02, at.z), new THREE.Vector3(at.x + w, at.y + 0.02, at.z), new THREE.Vector3(at.x + w, at.y + 0.02, at.z + d), new THREE.Vector3(at.x, at.y + 0.02, at.z + d)];
    if (type === 'rect') return lineOf(corners, true);
    const plane = new THREE.Mesh(
      new THREE.PlaneGeometry(Math.max(0.05, w), Math.max(0.05, h)),
      new THREE.MeshBasicMaterial({ color, transparent: true, opacity: Math.min(0.5, Math.max(0.1, alpha)), side: THREE.DoubleSide, depthWrite: false }),
    );
    plane.rotation.x = -Math.PI / 2;
    plane.position.set(at.x + w / 2, at.y + 0.03, at.z + d / 2);
    return plane;
  }
  if (type === 'box') {
    const edges = new THREE.LineSegments(new THREE.EdgesGeometry(new THREE.BoxGeometry(Math.max(0.1, g.width || 1), Math.max(0.1, g.height || 1), Math.max(0.1, g.radius || g.width || 1))), line);
    edges.position.copy(at);
    return edges;
  }
  const sphere = new THREE.Mesh(new THREE.SphereGeometry(Math.max(0.05, g.radius || 0.35), 16, 10), new THREE.MeshBasicMaterial({ color, wireframe: true, transparent: true, opacity: Math.min(0.5, alpha) }));
  sphere.position.copy(at);
  return sphere;
}

function dispose(root: THREE.Object3D): void {
  root.traverse((node) => {
    const mesh = node as THREE.Mesh;
    mesh.geometry?.dispose();
    const material = mesh.material as THREE.Material | THREE.Material[] | undefined;
    if (Array.isArray(material)) material.forEach((m) => m.dispose());
    else material?.dispose();
  });
}
