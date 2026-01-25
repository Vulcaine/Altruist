// world-renderer.ts
import * as THREE from 'three';
import { HeightfieldDto, WorldObjectDto } from './models/world.model';
import { WorldInputController } from './world-input.controller';

// Mirror of backend PhysxColliderShape3D enum
enum PhysxColliderShape3D {
  Sphere3D = 0,
  Box3D = 1,
  Capsule3D = 2,
  Heightfield3D = 3,
}

export interface CameraInfo {
  position: { x: number; y: number; z: number };
  rotation: { yaw: number; pitch: number; roll: number };
}

export class WorldRenderer {
  scene?: THREE.Scene;
  camera?: THREE.PerspectiveCamera;
  renderer?: THREE.WebGLRenderer;

  private collidersGroup: THREE.Group | null = null;

  private frameId: number | null = null;
  private lastFrameTime = performance.now();
  private resizeHandler?: () => void;

  private followSelection = true;
  private lastSelectedId: string | null = null;

  /** ✅ meshes created for each object instanceId (for accurate zoom/focus) */
  private meshesByObjectId = new Map<string, THREE.Object3D[]>();

  constructor(
    private readonly input: WorldInputController,
    private readonly onCameraInfo?: (info: CameraInfo) => void,
  ) {}

  init(container: HTMLDivElement): void {
    const width = container.clientWidth || container.offsetWidth || 640;
    const height = container.clientHeight || container.offsetHeight || 480;

    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color(0x020617);

    this.camera = new THREE.PerspectiveCamera(60, width / height, 0.1, 100000);
    this.camera.position.set(0, 0, 0);

    const ambient = new THREE.AmbientLight(0xffffff, 0.4);
    this.scene.add(ambient);

    const dir = new THREE.DirectionalLight(0xffffff, 0.6);
    dir.position.set(100, 200, 100);
    this.scene.add(dir);

    this.renderer = new THREE.WebGLRenderer({ antialias: true });
    this.renderer.setSize(width, height);
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));

    container.innerHTML = '';
    container.appendChild(this.renderer.domElement);

    this.resizeHandler = () => {
      if (!this.renderer || !this.camera) return;

      const w = container.clientWidth || container.offsetWidth || 640;
      const h = container.clientHeight || container.offsetHeight || 480;

      this.camera.aspect = w / h;
      this.camera.updateProjectionMatrix();
      this.renderer.setSize(w, h);
    };

    window.addEventListener('resize', this.resizeHandler);

    this.input.onUserMove = () => {
      // user moved the camera manually -> stop auto-follow until they click a selection again
      this.followSelection = false;
    };

    this.input.attach();
    this.lastFrameTime = performance.now();
  }

  startLoop(): void {
    if (!this.renderer || !this.scene || !this.camera) return;

    const animate = () => {
      const now = performance.now();
      const dt = (now - this.lastFrameTime) / 1000;
      this.lastFrameTime = now;

      this.input.updateCamera(this.camera!, dt);

      if (this.onCameraInfo && this.camera) {
        const euler = new THREE.Euler().setFromQuaternion(
          this.camera.quaternion,
          'YXZ',
        );

        this.onCameraInfo({
          position: {
            x: this.camera.position.x,
            y: this.camera.position.y,
            z: this.camera.position.z,
          },
          rotation: {
            yaw: THREE.MathUtils.radToDeg(euler.y),
            pitch: THREE.MathUtils.radToDeg(euler.x),
            roll: THREE.MathUtils.radToDeg(euler.z),
          },
        });
      }

      this.renderer!.render(this.scene!, this.camera!);
      this.frameId = requestAnimationFrame(animate);
    };

    this.frameId = requestAnimationFrame(animate);
  }

  dispose(): void {
    if (this.frameId !== null) {
      cancelAnimationFrame(this.frameId);
      this.frameId = null;
    }

    if (this.renderer) {
      this.renderer.dispose();
      this.renderer = undefined;
    }

    if (this.collidersGroup && this.scene) {
      this.scene.remove(this.collidersGroup);
      this.disposeGroupMeshes(this.collidersGroup);
      this.collidersGroup = null;
    }

    if (this.resizeHandler) {
      window.removeEventListener('resize', this.resizeHandler);
      this.resizeHandler = undefined;
    }

    this.input.detach();
    this.meshesByObjectId.clear();
  }

  private disposeGroupMeshes(group: THREE.Object3D) {
    group.traverse((obj) => {
      const mesh = obj as THREE.Mesh;
      if ((mesh as any).geometry) {
        (mesh as any).geometry.dispose?.();
      }
      const mat = (mesh as any).material;
      if (Array.isArray(mat)) mat.forEach((m) => m.dispose?.());
      else mat?.dispose?.();
    });
  }

  rebuildColliders(
    objects: WorldObjectDto[],
    selected?: WorldObjectDto | null,
  ): void {
    if (!this.scene) return;

    const camera = this.camera;
    const isCameraAtOrigin =
      !!camera &&
      camera.position.x === 0 &&
      camera.position.y === 0 &&
      camera.position.z === 0;

    // Detect selection change
    const selectedId = selected?.instanceId ?? null;
    const selectionChanged = selectedId !== this.lastSelectedId;
    this.lastSelectedId = selectedId;

    // If selection changed -> resume following it
    if (selectionChanged && selectedId !== null) {
      this.followSelection = true;
    }

    // Remove old group
    if (this.collidersGroup) {
      this.scene.remove(this.collidersGroup);
      this.disposeGroupMeshes(this.collidersGroup);
      this.collidersGroup = null;
    }

    // ✅ rebuild maps each refresh
    this.meshesByObjectId.clear();

    const group = new THREE.Group();

    for (const obj of objects) {
      const objMeshes: THREE.Object3D[] = [];

      if (obj.colliders && obj.colliders.length > 0) {
        for (const col of obj.colliders) {
          const t = col.transform ?? obj.transform;

          if (col.heightfield) {
            const mesh = this.createHeightfieldMesh(col.heightfield);
            mesh.position.set(t.position.x, t.position.y, t.position.z);
            mesh.scale.set(t.scale.x || 1, t.scale.y || 1, t.scale.z || 1);

            group.add(mesh);
            objMeshes.push(mesh);
          } else {
            const mesh = this.createColliderMesh(col, t);
            group.add(mesh);
            objMeshes.push(mesh);
          }
        }
      } else {
        // Fallback: visualize the whole object as a box
        const t = obj.transform;

        const sx = Math.max(0.1, t.size.x * t.scale.x);
        const sy = Math.max(0.1, t.size.y * t.scale.y);
        const sz = Math.max(0.1, t.size.z * t.scale.z);

        const geom = new THREE.BoxGeometry(sx, sy, sz);
        const mat = new THREE.MeshBasicMaterial({
          color: 0x22c55e,
          wireframe: true,
        });

        const mesh = new THREE.Mesh(geom, mat);
        mesh.position.set(t.position.x, t.position.y, t.position.z);

        group.add(mesh);
        objMeshes.push(mesh);
      }

      this.meshesByObjectId.set(obj.instanceId, objMeshes);
    }

    this.scene.add(group);
    this.collidersGroup = group;

    if (!camera) return;

    // ✅ Always focus to selection when clicked (even on refresh)
    if (selected && this.followSelection) {
      this.focusOnObject(selected);
      return;
    }

    // only auto-frame once
    if (isCameraAtOrigin) {
      if (objects.length > 0) {
        this.focusOnObject(objects[0]);
      } else {
        this.fitCameraToGroup();
      }
    }
  }

  private createColliderMesh(
    col: { shape: PhysxColliderShape3D | number; transform?: any },
    t: any,
  ): THREE.Mesh {
    const shape = col.shape as number;

    const sx = Math.max(0.0001, t.size.x * t.scale.x);
    const sy = Math.max(0.0001, t.size.y * t.scale.y);
    const sz = Math.max(0.0001, t.size.z * t.scale.z);

    let geom: THREE.BufferGeometry;

    switch (shape) {
      case PhysxColliderShape3D.Sphere3D: {
        const radius = Math.max(0.05, sx);
        geom = new THREE.SphereGeometry(radius, 16, 12);
        break;
      }

      case PhysxColliderShape3D.Capsule3D: {
        const radius = Math.max(0.05, sx);
        const halfLength = Math.max(0, sy);
        const cylLength = Math.max(0.05, halfLength * 2);

        const CapsuleGeomCtor = (THREE as any).CapsuleGeometry;
        if (typeof CapsuleGeomCtor === 'function') {
          geom = new CapsuleGeomCtor(radius, cylLength, 8, 16);
        } else {
          const height = cylLength + 2 * radius;
          geom = new THREE.CylinderGeometry(radius, radius, height, 8, 1, true);
        }
        break;
      }

      case PhysxColliderShape3D.Box3D:
      default: {
        const fullX = Math.max(0.1, sx * 2);
        const fullY = Math.max(0.1, sy * 2);
        const fullZ = Math.max(0.1, sz * 2);
        geom = new THREE.BoxGeometry(fullX, fullY, fullZ);
        break;
      }
    }

    const mat = new THREE.MeshBasicMaterial({
      color: 0x22c55e,
      wireframe: true,
    });

    const mesh = new THREE.Mesh(geom, mat);
    mesh.position.set(t.position.x, t.position.y, t.position.z);
    return mesh;
  }

  private createHeightfieldMesh(hf: HeightfieldDto): THREE.Mesh {
    const width = hf.width;
    const height = hf.height;
    const cellSizeX = hf.cellSizeX;
    const cellSizeZ = hf.cellSizeZ;

    const vertCount = width * height;
    const positions = new Float32Array(vertCount * 3);

    let idx = 0;
    for (let z = 0; z < height; z++) {
      for (let x = 0; x < width; x++) {
        const h = hf.heights[x][z];
        positions[idx++] = x * cellSizeX;
        positions[idx++] = h;
        positions[idx++] = z * cellSizeZ;
      }
    }

    const indices: number[] = [];
    for (let z = 0; z < height - 1; z++) {
      for (let x = 0; x < width - 1; x++) {
        const i00 = z * width + x;
        const i10 = z * width + (x + 1);
        const i01 = (z + 1) * width + x;
        const i11 = (z + 1) * width + (x + 1);

        indices.push(i00, i01, i10);
        indices.push(i10, i01, i11);
      }
    }

    const geom = new THREE.BufferGeometry();
    geom.setAttribute('position', new THREE.BufferAttribute(positions, 3));
    geom.setIndex(indices);
    geom.computeVertexNormals();

    const mat = new THREE.MeshBasicMaterial({
      color: 0x22c55e,
      wireframe: true,
    });

    return new THREE.Mesh(geom, mat);
  }

  /**
   * ✅ Accurate focus using the meshes created for THIS object.
   * Avoids terrain-sized offsets and wrong transform sizes.
   */
  private focusOnObject(obj: WorldObjectDto): void {
    if (!this.camera) return;

    const meshes = this.meshesByObjectId.get(obj.instanceId) ?? [];

    let center = new THREE.Vector3(
      obj.transform.position.x,
      obj.transform.position.y,
      obj.transform.position.z,
    );
    let maxDim = 1;

    if (meshes.length > 0) {
      const box = new THREE.Box3();

      for (const m of meshes) {
        const b = new THREE.Box3().setFromObject(m);
        box.union(b);
      }

      const size = box.getSize(new THREE.Vector3());
      center = box.getCenter(new THREE.Vector3());

      maxDim = Math.max(size.x, size.y, size.z);
      if (!isFinite(maxDim) || maxDim <= 0) maxDim = 1;
    }

    // compute a good distance based on FOV (zoom *in*, not far away)
    const fov = (this.camera.fov * Math.PI) / 180;
    let distance = maxDim / (2 * Math.tan(fov / 2));

    // some padding so it isn’t inside the object
    distance = distance * 1.6 + 2;

    // clamp
    distance = THREE.MathUtils.clamp(distance, 2, 400);

    // nicer viewing direction (diagonal + slightly above)
    const viewDir = new THREE.Vector3(1, 0.65, 1).normalize();

    this.camera.position.set(
      center.x + viewDir.x * distance,
      center.y + viewDir.y * distance,
      center.z + viewDir.z * distance,
    );

    const dir = new THREE.Vector3()
      .subVectors(center, this.camera.position)
      .normalize();
    this.input.setOrientationFromDirection(dir);

    this.camera.lookAt(center);
  }

  private fitCameraToGroup(): void {
    if (!this.camera || !this.collidersGroup) return;

    const box = new THREE.Box3().setFromObject(this.collidersGroup);
    const size = box.getSize(new THREE.Vector3());
    const center = box.getCenter(new THREE.Vector3());

    if (!isFinite(size.x) || !isFinite(size.y) || !isFinite(size.z)) return;

    const maxDim = Math.max(size.x, size.y, size.z);
    const fov = (this.camera.fov * Math.PI) / 180;
    const distance = maxDim / (2 * Math.tan(fov / 2)) + maxDim;

    this.camera.position.set(
      center.x + distance * 0.6,
      center.y + distance * 0.4,
      center.z + distance,
    );

    const forward = new THREE.Vector3()
      .subVectors(center, this.camera.position)
      .normalize();

    this.input.setOrientationFromDirection(forward);
    this.camera.lookAt(center);
  }
}
