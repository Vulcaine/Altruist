import * as THREE from 'three';

export class WorldInputController {
  private keys: Record<string, boolean> = {};
  private yaw = 0;
  private pitch = -0.3; // slight downward tilt

  private readonly moveSpeed = 80;
  private readonly turnSpeed = 1.5; // radians/s (Q/E)
  private readonly pitchSpeed = 1.2; // radians/s (R/F)

  private keyDownHandler = (event: KeyboardEvent) => {
    this.keys[event.key.toLowerCase()] = true;
  };

  private keyUpHandler = (event: KeyboardEvent) => {
    this.keys[event.key.toLowerCase()] = false;
  };

  /** Called whenever the user actually moves/rotates the camera (WASD/QE/RF). */
  onUserMove?: () => void;

  attach(): void {
    window.addEventListener('keydown', this.keyDownHandler);
    window.addEventListener('keyup', this.keyUpHandler);
  }

  detach(): void {
    window.removeEventListener('keydown', this.keyDownHandler);
    window.removeEventListener('keyup', this.keyUpHandler);
  }

  updateCamera(camera: THREE.PerspectiveCamera, dt: number): void {
    let moved = false;

    // Yaw: Q/E
    if (this.keys['q']) {
      this.yaw += this.turnSpeed * dt;
      moved = true;
    }
    if (this.keys['e']) {
      this.yaw -= this.turnSpeed * dt;
      moved = true;
    }

    // Pitch: R/F
    if (this.keys['r']) {
      this.pitch += this.pitchSpeed * dt;
      moved = true;
    }
    if (this.keys['f']) {
      this.pitch -= this.pitchSpeed * dt;
      moved = true;
    }

    // Clamp pitch
    const maxPitch = Math.PI / 2 - 0.1;
    if (this.pitch > maxPitch) this.pitch = maxPitch;
    if (this.pitch < -maxPitch) this.pitch = -maxPitch;

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

    if (this.keys['w']) {
      vel.add(forward);
      moved = true;
    }
    if (this.keys['s']) {
      vel.sub(forward);
      moved = true;
    }
    if (this.keys['a']) {
      vel.sub(right);
      moved = true;
    }
    if (this.keys['d']) {
      vel.add(right);
      moved = true;
    }

    if (vel.lengthSq() > 0) {
      vel.normalize().multiplyScalar(this.moveSpeed * dt);
      camera.position.add(vel);
    }

    const lookTarget = new THREE.Vector3().copy(camera.position).add(forward);
    camera.lookAt(lookTarget);

    if (moved && this.onUserMove) {
      this.onUserMove();
    }
  }

  /** Used when we focus on an object so yaw/pitch follow the new view direction. */
  setOrientationFromDirection(dir: THREE.Vector3): void {
    this.pitch = Math.asin(dir.y);
    this.yaw = Math.atan2(dir.x, dir.z);
  }
}
