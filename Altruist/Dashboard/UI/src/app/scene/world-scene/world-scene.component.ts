import { CommonModule } from '@angular/common';
import {
  AfterViewInit,
  Component,
  ElementRef,
  Input,
  OnChanges,
  OnDestroy,
  SimpleChanges,
  ViewChild,
} from '@angular/core';
import * as THREE from 'three';
import {
  HeightfieldDto,
  WorldObjectDto,
  WorldSummary,
} from '../models/world.model';

@Component({
  selector: 'app-world-scene',
  standalone: true,
  imports: [CommonModule],
  templateUrl: './world-scene.component.html',
  styleUrl: './world-scene.component.scss',
})
export class WorldSceneComponent
  implements AfterViewInit, OnChanges, OnDestroy
{
  @Input() world: WorldSummary | null = null;
  @Input() objects: WorldObjectDto[] = [];
  @Input() isLoading = false;
  @Input() selectedObject: WorldObjectDto | null = null;

  @ViewChild('viewport')
  viewportRef?: ElementRef<HTMLDivElement>;

  private scene?: THREE.Scene;
  private camera?: THREE.PerspectiveCamera;
  private renderer?: THREE.WebGLRenderer;
  private frameId: number | null = null;

  private collidersGroup: THREE.Group | null = null;

  private hasThreeInitialized = false;
  private viewInitialized = false;

  private keys: Record<string, boolean> = {};
  private yaw = 0;
  private pitch = -0.3; // fixed slight downward tilt

  private lastFrameTime = performance.now();

  ngAfterViewInit(): void {
    this.viewInitialized = true;
    this.tryInitThree();
  }

  ngOnChanges(changes: SimpleChanges): void {
    if (changes['world'] && this.world && this.viewInitialized) {
      this.tryInitThree();
    }

    if (this.scene && changes['objects'] && !changes['objects'].firstChange) {
      this.rebuildColliders();
    }

    if (
      this.scene &&
      this.camera &&
      changes['selectedObject'] &&
      this.selectedObject
    ) {
      this.focusOnObject(this.selectedObject);
    }
  }

  ngOnDestroy(): void {
    if (this.frameId !== null) {
      cancelAnimationFrame(this.frameId);
    }
    if (this.renderer) {
      this.renderer.dispose();
    }

    window.removeEventListener('resize', this.onWindowResize);
    window.removeEventListener('keydown', this.onKeyDown);
    window.removeEventListener('keyup', this.onKeyUp);
  }

  // ────────────────────────────────────────────────────────────────
  // Initialization guard
  // ────────────────────────────────────────────────────────────────

  private tryInitThree(): void {
    if (this.hasThreeInitialized) return;
    if (!this.world) return;
    if (!this.viewportRef) return;

    this.initThree();
    this.rebuildColliders();
    this.startRenderingLoop();
    this.hasThreeInitialized = true;
  }

  // ────────────────────────────────────────────────────────────────
  // Three.js setup
  // ────────────────────────────────────────────────────────────────

  private initThree(): void {
    const container = this.viewportRef!.nativeElement;
    const width = container.clientWidth || container.offsetWidth || 640;
    const height = container.clientHeight || container.offsetHeight || 480;

    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color(0x020617);

    this.camera = new THREE.PerspectiveCamera(60, width / height, 0.1, 10000);
    this.camera.position.set(0, 150, 250);

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

    window.addEventListener('resize', this.onWindowResize);
    window.addEventListener('keydown', this.onKeyDown);
    window.addEventListener('keyup', this.onKeyUp);
  }

  private onWindowResize = () => {
    if (!this.renderer || !this.camera || !this.viewportRef) return;

    const container = this.viewportRef.nativeElement;
    const width = container.clientWidth || container.offsetWidth || 640;
    const height = container.clientHeight || container.offsetHeight || 480;

    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(width, height);
  };

  // ────────────────────────────────────────────────────────────────
  // Input handling (WASD + Q/E yaw, R/F pitch)
  // ────────────────────────────────────────────────────────────────

  private onKeyDown = (event: KeyboardEvent) => {
    this.keys[event.key.toLowerCase()] = true;
  };

  private onKeyUp = (event: KeyboardEvent) => {
    this.keys[event.key.toLowerCase()] = false;
  };

  private updateCamera(dt: number): void {
    if (!this.camera) return;

    const moveSpeed = 80;
    const turnSpeed = 1.5; // yaw speed (Q/E), radians per second
    const pitchSpeed = 1.2; // pitch speed (R/F), radians per second

    // Yaw: turn left/right with Q/E
    if (this.keys['q']) {
      this.yaw += turnSpeed * dt; // turn left
    }
    if (this.keys['e']) {
      this.yaw -= turnSpeed * dt; // turn right
    }

    // Pitch: look up/down with R/F
    if (this.keys['r']) {
      this.pitch += pitchSpeed * dt; // look up
    }
    if (this.keys['f']) {
      this.pitch -= pitchSpeed * dt; // look down
    }

    // Clamp pitch to avoid flipping over
    const maxPitch = Math.PI / 2 - 0.1; // ~±80 degrees
    if (this.pitch > maxPitch) this.pitch = maxPitch;
    if (this.pitch < -maxPitch) this.pitch = -maxPitch;

    // Recompute forward/right vectors from yaw + pitch
    const forward = new THREE.Vector3(
      Math.cos(this.pitch) * Math.sin(this.yaw),
      Math.sin(this.pitch),
      Math.cos(this.pitch) * Math.cos(this.yaw)
    );

    const right = new THREE.Vector3(
      Math.sin(this.yaw - Math.PI / 2),
      0,
      Math.cos(this.yaw - Math.PI / 2)
    );

    const vel = new THREE.Vector3();

    if (this.keys['w']) vel.add(forward);
    if (this.keys['s']) vel.sub(forward);
    if (this.keys['a']) vel.sub(right);
    if (this.keys['d']) vel.add(right);

    if (vel.lengthSq() > 0) {
      vel.normalize().multiplyScalar(moveSpeed * dt);
      this.camera.position.add(vel);
    }

    const lookTarget = new THREE.Vector3()
      .copy(this.camera.position)
      .add(forward);
    this.camera.lookAt(lookTarget);
  }

  private startRenderingLoop(): void {
    const animate = () => {
      const now = performance.now();
      const dt = (now - this.lastFrameTime) / 1000;
      this.lastFrameTime = now;

      this.updateCamera(dt);
      this.renderer!.render(this.scene!, this.camera!);

      this.frameId = requestAnimationFrame(animate);
    };
    this.frameId = requestAnimationFrame(animate);
  }

  // ────────────────────────────────────────────────────────────────
  // Wireframe colliders
  // ────────────────────────────────────────────────────────────────

  private rebuildColliders(): void {
    if (!this.scene) return;

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

    for (const obj of this.objects) {
      // Prefer collider descriptors if present
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
        // Fallback: draw a single box from the object's transform
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

    if (this.objects.length > 0) {
      // prefer focusing selected object if present
      if (this.selectedObject) {
        this.focusOnObject(this.selectedObject);
      } else {
        this.focusOnObject(this.objects[0]);
      }
    } else {
      this.fitCameraToGroup();
    }
  }

  /** Build a wireframe mesh from heightfield data (terrain). */
  private createHeightfieldMesh(hf: HeightfieldDto): THREE.Mesh {
    const width = hf.width;
    const height = hf.height;
    const cellSizeX = hf.cellSizeX;
    const cellSizeZ = hf.cellSizeZ;

    // Heights are already in world units (0..size.y), so don't apply heightScale again.
    const vertCount = width * height;
    const positions = new Float32Array(vertCount * 3);

    let idx = 0;
    for (let z = 0; z < height; z++) {
      for (let x = 0; x < width; x++) {
        const h = hf.heights[x][z]; // <-- no * hf.heightScale

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

    this.pitch = Math.asin(dir.y);
    this.yaw = Math.atan2(dir.x, dir.z);

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
    this.pitch = Math.asin(forward.y);
    this.yaw = Math.atan2(forward.x, forward.z);

    this.camera.lookAt(center);
  }
}
