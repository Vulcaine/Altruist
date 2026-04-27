import * as THREE from 'three';

const WORLD_UP = new THREE.Vector3(0, 1, 0);
const MIN_PITCH = -Math.PI / 2 + 0.02;
const MAX_PITCH = Math.PI / 2 - 0.02;

export class WorldInputController {
  private domElement?: HTMLElement;
  private keys: Record<string, boolean> = {};
  private pressedButtons = 0;
  private activePointerId: number | null = null;
  private lastX = 0;
  private lastY = 0;
  private yaw = Math.PI;
  private pitch = -0.35;
  private pivot = new THREE.Vector3(0, 0, 0);
  private pivotRadius = 40;
  private lastFocus:
    | { center: THREE.Vector3; radius: number }
    | null = null;

  private readonly baseMoveSpeed = 90;
  private readonly lookSensitivity = 0.003;
  private readonly orbitSensitivity = 0.006;
  private readonly panScale = 0.0018;
  private readonly dollyScale = 0.0015;

  /** Called whenever the user actually moves/rotates the camera. */
  onUserMove?: () => void;

  private keyDownHandler = (event: KeyboardEvent) => {
    const key = event.key.toLowerCase();
    this.keys[key] = true;

    if (key === 'f' && this.lastFocus && this.domElement) {
      event.preventDefault();
      this.requestFocus?.(this.lastFocus.center, this.lastFocus.radius);
    }
  };

  private keyUpHandler = (event: KeyboardEvent) => {
    this.keys[event.key.toLowerCase()] = false;
  };

  private pointerDownHandler = (event: PointerEvent) => {
    if (!this.domElement) return;

    this.domElement.focus();
    this.activePointerId = event.pointerId;
    this.lastX = event.clientX;
    this.lastY = event.clientY;
    this.pressedButtons = event.buttons;
    this.domElement.setPointerCapture?.(event.pointerId);

    if (this.isNavigationPointer(event)) {
      event.preventDefault();
    }
  };

  private pointerMoveHandler = (event: PointerEvent) => {
    if (!this.domElement || event.pointerId !== this.activePointerId) return;

    const dx = event.clientX - this.lastX;
    const dy = event.clientY - this.lastY;
    this.lastX = event.clientX;
    this.lastY = event.clientY;
    this.pressedButtons = event.buttons;

    if (this.isRightButton(event)) {
      this.yaw -= dx * this.lookSensitivity;
      this.pitch = THREE.MathUtils.clamp(
        this.pitch - dy * this.lookSensitivity,
        MIN_PITCH,
        MAX_PITCH,
      );
      this.markMoved();
      event.preventDefault();
      return;
    }

    if (this.isMiddleButton(event)) {
      this.panByScreenDelta(dx, dy, this.pivotRadius);
      this.markMoved();
      event.preventDefault();
      return;
    }

    if (this.isLeftButton(event) && event.altKey) {
      this.orbitByScreenDelta(dx, dy);
      this.markMoved();
      event.preventDefault();
    }
  };

  private pointerUpHandler = (event: PointerEvent) => {
    if (event.pointerId !== this.activePointerId) return;

    this.pressedButtons = event.buttons;
    if (event.buttons === 0) {
      this.activePointerId = null;
    }
  };

  private wheelHandler = (event: WheelEvent) => {
    if (!this.domElement || !this.currentCamera) return;

    event.preventDefault();
    const distance = Math.max(1, this.pivotRadius);
    const amount = event.deltaY * this.dollyScale * distance;
    const forward = this.getForward();

    this.currentCamera.position.addScaledVector(forward, amount);
    this.pivot.addScaledVector(forward, amount);
    this.pivotRadius = Math.max(1, this.currentCamera.position.distanceTo(this.pivot));
    this.markMoved();
  };

  private contextMenuHandler = (event: MouseEvent) => {
    event.preventDefault();
  };

  private currentCamera?: THREE.PerspectiveCamera;

  /**
   * The renderer owns exact object framing. Pressing F calls back into it so
   * focus uses the same bounds as selection focus.
   */
  requestFocus?: (center: THREE.Vector3, radius: number) => void;

  attach(domElement: HTMLElement): void {
    this.detach();
    this.domElement = domElement;
    this.domElement.tabIndex = 0;
    this.domElement.style.outline = 'none';

    window.addEventListener('keydown', this.keyDownHandler);
    window.addEventListener('keyup', this.keyUpHandler);
    domElement.addEventListener('pointerdown', this.pointerDownHandler);
    domElement.addEventListener('pointermove', this.pointerMoveHandler);
    domElement.addEventListener('pointerup', this.pointerUpHandler);
    domElement.addEventListener('pointerleave', this.pointerUpHandler);
    domElement.addEventListener('wheel', this.wheelHandler, { passive: false });
    domElement.addEventListener('contextmenu', this.contextMenuHandler);
  }

  detach(): void {
    window.removeEventListener('keydown', this.keyDownHandler);
    window.removeEventListener('keyup', this.keyUpHandler);

    if (this.domElement) {
      this.domElement.removeEventListener('pointerdown', this.pointerDownHandler);
      this.domElement.removeEventListener('pointermove', this.pointerMoveHandler);
      this.domElement.removeEventListener('pointerup', this.pointerUpHandler);
      this.domElement.removeEventListener('pointerleave', this.pointerUpHandler);
      this.domElement.removeEventListener('wheel', this.wheelHandler);
      this.domElement.removeEventListener('contextmenu', this.contextMenuHandler);
    }

    this.domElement = undefined;
    this.currentCamera = undefined;
    this.activePointerId = null;
    this.pressedButtons = 0;
    this.keys = {};
  }

  updateCamera(camera: THREE.PerspectiveCamera, dt: number): void {
    this.currentCamera = camera;

    const velocity = new THREE.Vector3();
    const forward = this.getForward();
    const right = this.getRight(forward);

    if (this.keys['w']) velocity.add(forward);
    if (this.keys['s']) velocity.sub(forward);
    if (this.keys['d']) velocity.add(right);
    if (this.keys['a']) velocity.sub(right);
    if (this.keys['e']) velocity.add(WORLD_UP);
    if (this.keys['q']) velocity.sub(WORLD_UP);

    if (velocity.lengthSq() > 0) {
      const speed = this.baseMoveSpeed * this.getSpeedMultiplier();
      velocity.normalize().multiplyScalar(speed * dt);
      camera.position.add(velocity);
      this.pivot.add(velocity);
      this.markMoved();
    }

    camera.quaternion.setFromEuler(new THREE.Euler(this.pitch, this.yaw, 0, 'YXZ'));
    this.pivotRadius = Math.max(1, camera.position.distanceTo(this.pivot));
  }

  /** Used when we focus on an object so yaw/pitch follow the new view direction. */
  setOrientationFromDirection(dir: THREE.Vector3): void {
    const normalized = dir.clone().normalize();
    this.pitch = THREE.MathUtils.clamp(Math.asin(normalized.y), MIN_PITCH, MAX_PITCH);
    this.yaw = Math.atan2(-normalized.x, -normalized.z);
  }

  setFocusTarget(center: THREE.Vector3, pivotRadius: number, focusSize = pivotRadius): void {
    this.pivot.copy(center);
    this.pivotRadius = Math.max(1, pivotRadius);
    this.lastFocus = {
      center: center.clone(),
      radius: Math.max(1, focusSize),
    };
  }

  private getForward(): THREE.Vector3 {
    return new THREE.Vector3(0, 0, -1)
      .applyEuler(new THREE.Euler(this.pitch, this.yaw, 0, 'YXZ'))
      .normalize();
  }

  private getRight(forward = this.getForward()): THREE.Vector3 {
    return new THREE.Vector3().crossVectors(forward, WORLD_UP).normalize();
  }

  private getUp(forward = this.getForward()): THREE.Vector3 {
    return new THREE.Vector3().crossVectors(this.getRight(forward), forward).normalize();
  }

  private getSpeedMultiplier(): number {
    if (this.keys['shift']) return 4;
    if (this.keys['alt']) return 0.25;
    return 1;
  }

  private panByScreenDelta(dx: number, dy: number, distance: number): void {
    if (!this.currentCamera) return;

    const forward = this.getForward();
    const right = this.getRight(forward);
    const up = this.getUp(forward);
    const scale = Math.max(0.05, distance * this.panScale);
    const delta = new THREE.Vector3()
      .addScaledVector(right, -dx * scale)
      .addScaledVector(up, dy * scale);

    this.currentCamera.position.add(delta);
    this.pivot.add(delta);
  }

  private orbitByScreenDelta(dx: number, dy: number): void {
    if (!this.currentCamera) return;

    this.yaw -= dx * this.orbitSensitivity;
    this.pitch = THREE.MathUtils.clamp(
      this.pitch - dy * this.orbitSensitivity,
      MIN_PITCH,
      MAX_PITCH,
    );

    const forward = this.getForward();
    this.currentCamera.position.copy(this.pivot).addScaledVector(forward, -this.pivotRadius);
  }

  private isNavigationPointer(event: PointerEvent): boolean {
    return this.isRightButton(event) || this.isMiddleButton(event) || (this.isLeftButton(event) && event.altKey);
  }

  private isLeftButton(event: PointerEvent): boolean {
    return (event.buttons & 1) !== 0;
  }

  private isRightButton(event: PointerEvent): boolean {
    return (event.buttons & 2) !== 0;
  }

  private isMiddleButton(event: PointerEvent): boolean {
    return (event.buttons & 4) !== 0;
  }

  private markMoved(): void {
    this.onUserMove?.();
  }
}
