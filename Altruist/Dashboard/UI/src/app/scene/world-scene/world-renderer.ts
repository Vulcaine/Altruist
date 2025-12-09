// world-renderer.ts
import * as THREE from 'three';
import { HeightfieldDto, WorldObjectDto } from './models/world.model';
import { WorldInputController } from './world-input.controller';

export class WorldRenderer {
  scene?: THREE.Scene;
  camera?: THREE.PerspectiveCamera;
  renderer?: THREE.WebGLRenderer;
  private collidersGroup: THREE.Group | null = null;

  private frameId: number | null = null;
  private lastFrameTime = performance.now();
  private resizeHandler?: () => void;

  constructor(private readonly input: WorldInputController) {}

  init(container: HTMLDivElement): void {
    const width = container.clientWidth || container.offsetWidth || 640;
    const height = container.clientHeight || container.offsetHeight || 480;

    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color(0x020617);

    this.camera = new THREE.PerspectiveCamera(60, width / height, 0.1, 10000);
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
      this.collidersGroup.traverse((obj) => {
        const mesh = obj as THREE.Mesh;
        if (mesh.geometry) mesh.geometry.dispose();
        if (Array.isArray(mesh.material)) {
          mesh.material.forEach((m) => m.dispose());
        } else if (mesh.material) {
          mesh.material.dispose();
        }
      });
      this.collidersGroup = null;
    }

    if (this.resizeHandler) {
      window.removeEventListener('resize', this.resizeHandler);
      this.resizeHandler = undefined;
    }

    this.input.detach();
  }

  rebuildColliders(
    objects: WorldObjectDto[],
    selected?: WorldObjectDto | null
  ): void {
    if (!this.scene) return;

    const shouldFrameCamera =
      this.camera &&
      this.camera.position.x === 0 &&
      this.camera.position.y === 0 &&
      this.camera.position.z === 0;

    if (this.collidersGroup) {
      this.scene.remove(this.collidersGroup);
      this.collidersGroup.traverse((obj) => {
        const mesh = obj as THREE.Mesh;
        if (mesh.geometry) mesh.geometry.dispose();
        if (Array.isArray(mesh.material)) {
          mesh.material.forEach((m) => m.dispose());
        } else if (mesh.material) {
          mesh.material.dispose();
        }
      });
      this.collidersGroup = null;
    }

    const group = new THREE.Group();

    for (const obj of objects) {
      if (obj.colliders && obj.colliders.length > 0) {
        for (const col of obj.colliders) {
          const t = col.transform ?? obj.transform;

          if (col.heightfield) {
            const mesh = this.createHeightfieldMesh(col.heightfield);
            mesh.position.set(t.position.x, t.position.y, t.position.z);
            mesh.scale.set(t.scale.x || 1, t.scale.y || 1, t.scale.z || 1);

            group.add(mesh);
          } else {
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
          }
        }
      } else {
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
      }
    }

    this.scene.add(group);
    this.collidersGroup = group;

    if (shouldFrameCamera) {
      if (objects.length > 0) {
        if (selected) {
          this.focusOnObject(selected);
        } else {
          this.focusOnObject(objects[0]);
        }
      } else {
        this.fitCameraToGroup();
      }
    }
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

  private focusOnObject(obj: WorldObjectDto): void {
    if (!this.camera) return;

    const t = obj.transform;

    const center = new THREE.Vector3(t.position.x, t.position.y, t.position.z);

    const size = new THREE.Vector3(
      t.size.x * t.scale.x,
      t.size.y * t.scale.y,
      t.size.z * t.scale.z
    );
    const maxDim = Math.max(size.x, size.y, size.z);
    const offset = maxDim * 3 || 50;

    this.camera.position.set(
      center.x + offset,
      center.y + offset * 0.5,
      center.z + offset
    );

    const dir = new THREE.Vector3()
      .subVectors(center, this.camera.position)
      .normalize();

    // Sync camera controller yaw/pitch with this new view direction
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
      center.x + distance * 0.5,
      center.y + distance * 0.5,
      center.z + distance
    );

    const forward = new THREE.Vector3()
      .subVectors(center, this.camera.position)
      .normalize();

    this.input.setOrientationFromDirection(forward);
    this.camera.lookAt(center);
  }
}
