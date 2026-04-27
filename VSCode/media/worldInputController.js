import * as THREE from "./vendor/three.module.js";

const WORLD_UP = new THREE.Vector3(0, 1, 0);
const MIN_PITCH = -Math.PI / 2 + 0.02;
const MAX_PITCH = Math.PI / 2 - 0.02;
const MIN_SPEED_MULTIPLIER = 0.5;
const MAX_SPEED_MULTIPLIER = 100;
const WHEEL_SPEED_SENSITIVITY = 0.0025;

export class WorldInputController {
  constructor() {
    this.domElement = null;
    this.currentCamera = null;
    this.keys = {};
    this.pressedButtons = 0;
    this.activePointerId = null;
    this.lastX = 0;
    this.lastY = 0;
    this.yaw = Math.PI;
    this.pitch = -0.35;
    this.pivot = new THREE.Vector3(0, 0, 0);
    this.pivotRadius = 40;
    this.lastFocus = null;
    this.baseMoveSpeed = 90;
    this.moveSpeedMultiplier = 1;
    this.lookSensitivity = 0.003;
    this.orbitSensitivity = 0.006;
    this.panScale = 0.0018;
    this.dollyScale = 0.0015;
    this.onUserMove = null;
    this.onSpeedChanged = null;
    this.requestFocus = null;

    this.handleKeyDown = this.handleKeyDown.bind(this);
    this.handleKeyUp = this.handleKeyUp.bind(this);
    this.handlePointerDown = this.handlePointerDown.bind(this);
    this.handlePointerMove = this.handlePointerMove.bind(this);
    this.handlePointerUp = this.handlePointerUp.bind(this);
    this.handleWheel = this.handleWheel.bind(this);
    this.handleContextMenu = this.handleContextMenu.bind(this);
  }

  attach(domElement) {
    this.detach();
    this.domElement = domElement;
    this.domElement.tabIndex = 0;
    this.domElement.style.outline = "none";

    window.addEventListener("keydown", this.handleKeyDown);
    window.addEventListener("keyup", this.handleKeyUp);
    domElement.addEventListener("pointerdown", this.handlePointerDown);
    domElement.addEventListener("pointermove", this.handlePointerMove);
    domElement.addEventListener("pointerup", this.handlePointerUp);
    domElement.addEventListener("pointerleave", this.handlePointerUp);
    domElement.addEventListener("wheel", this.handleWheel, { passive: false });
    domElement.addEventListener("contextmenu", this.handleContextMenu);
  }

  detach() {
    window.removeEventListener("keydown", this.handleKeyDown);
    window.removeEventListener("keyup", this.handleKeyUp);

    if (this.domElement) {
      this.domElement.removeEventListener("pointerdown", this.handlePointerDown);
      this.domElement.removeEventListener("pointermove", this.handlePointerMove);
      this.domElement.removeEventListener("pointerup", this.handlePointerUp);
      this.domElement.removeEventListener("pointerleave", this.handlePointerUp);
      this.domElement.removeEventListener("wheel", this.handleWheel);
      this.domElement.removeEventListener("contextmenu", this.handleContextMenu);
    }

    this.domElement = null;
    this.currentCamera = null;
    this.activePointerId = null;
    this.pressedButtons = 0;
    this.keys = {};
  }

  updateCamera(camera, dt = 1 / 60) {
    this.currentCamera = camera;

    const velocity = new THREE.Vector3();
    const forward = this.getForward();
    const right = this.getRight(forward);

    if (this.keys.w) velocity.add(forward);
    if (this.keys.s) velocity.sub(forward);
    if (this.keys.d) velocity.add(right);
    if (this.keys.a) velocity.sub(right);
    if (this.keys.e) velocity.add(WORLD_UP);
    if (this.keys.q) velocity.sub(WORLD_UP);

    if (velocity.lengthSq() > 0) {
      const speed = this.baseMoveSpeed * this.getSpeedMultiplier();
      velocity.normalize().multiplyScalar(speed * dt);
      camera.position.add(velocity);
      this.pivot.add(velocity);
      this.markMoved();
    }

    camera.quaternion.setFromEuler(new THREE.Euler(this.pitch, this.yaw, 0, "YXZ"));
    this.pivotRadius = Math.max(1, camera.position.distanceTo(this.pivot));
  }

  focus(bounds) {
    const center = bounds.center || new THREE.Vector3();
    const radius = Math.max(4, Number(bounds.radius || 8));
    this.focusCameraOnBounds(center, radius);
  }

  setTarget(target) {
    this.pivot.copy(target);
  }

  setFocusTarget(center, pivotRadius, focusSize = pivotRadius) {
    this.pivot.copy(center);
    this.pivotRadius = Math.max(1, pivotRadius);
    this.lastFocus = {
      center: center.clone(),
      radius: Math.max(1, focusSize),
    };
  }

  setOrientationFromDirection(dir) {
    const normalized = dir.clone().normalize();
    this.pitch = THREE.MathUtils.clamp(Math.asin(normalized.y), MIN_PITCH, MAX_PITCH);
    this.yaw = Math.atan2(-normalized.x, -normalized.z);
  }

  handleKeyDown(event) {
    const key = event.key.toLowerCase();
    this.keys[key] = true;

    if (key === "f" && this.lastFocus) {
      event.preventDefault();
      this.requestFocus?.(this.lastFocus.center, this.lastFocus.radius);
    }
  }

  handleKeyUp(event) {
    this.keys[event.key.toLowerCase()] = false;
  }

  handlePointerDown(event) {
    if (!this.domElement) {
      return;
    }

    this.domElement.focus();
    this.activePointerId = event.pointerId;
    this.lastX = event.clientX;
    this.lastY = event.clientY;
    this.pressedButtons = event.buttons;
    this.domElement.setPointerCapture?.(event.pointerId);

    if (this.isNavigationPointer(event)) {
      event.preventDefault();
    }
  }

  handlePointerMove(event) {
    if (!this.domElement || event.pointerId !== this.activePointerId) {
      return;
    }

    const dx = event.clientX - this.lastX;
    const dy = event.clientY - this.lastY;
    this.lastX = event.clientX;
    this.lastY = event.clientY;
    this.pressedButtons = event.buttons;

    if (this.isRightButton(event)) {
      this.yaw -= dx * this.lookSensitivity;
      this.pitch = THREE.MathUtils.clamp(this.pitch - dy * this.lookSensitivity, MIN_PITCH, MAX_PITCH);
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
  }

  handlePointerUp(event) {
    if (event.pointerId !== this.activePointerId) {
      return;
    }

    this.pressedButtons = event.buttons;
    if (event.buttons === 0) {
      this.activePointerId = null;
    }
  }

  handleWheel(event) {
    if (!this.currentCamera) {
      return;
    }

    event.preventDefault();

    if (this.isSpeedWheelMode()) {
      this.adjustMoveSpeedFromWheel(event.deltaY);
      return;
    }

    const distance = Math.max(1, this.pivotRadius);
    const amount = event.deltaY * this.dollyScale * distance;
    const forward = this.getForward();

    this.currentCamera.position.addScaledVector(forward, amount);
    this.pivot.addScaledVector(forward, amount);
    this.pivotRadius = Math.max(1, this.currentCamera.position.distanceTo(this.pivot));
    this.markMoved();
  }

  getMoveSpeedMultiplier() {
    return this.moveSpeedMultiplier;
  }

  setMoveSpeedMultiplier(value) {
    const numeric = Number(value);
    const next = THREE.MathUtils.clamp(
      Number.isFinite(numeric) ? numeric : 1,
      MIN_SPEED_MULTIPLIER,
      MAX_SPEED_MULTIPLIER
    );

    if (Math.abs(next - this.moveSpeedMultiplier) < 0.001) {
      return;
    }

    this.moveSpeedMultiplier = next;
    this.onSpeedChanged?.(this.moveSpeedMultiplier);
  }

  adjustMoveSpeedFromWheel(deltaY) {
    const factor = Math.pow(1 + WHEEL_SPEED_SENSITIVITY, -deltaY);
    this.setMoveSpeedMultiplier(this.moveSpeedMultiplier * factor);
  }

  handleContextMenu(event) {
    event.preventDefault();
  }

  focusCameraOnBounds(center, radius) {
    if (!this.currentCamera) {
      this.setFocusTarget(center, radius);
      return;
    }

    const distance = THREE.MathUtils.clamp(radius * 2.3, 6, 5000);
    const viewDir = new THREE.Vector3(1, 0.65, 1).normalize();

    this.currentCamera.position.set(
      center.x + viewDir.x * distance,
      center.y + viewDir.y * distance,
      center.z + viewDir.z * distance
    );

    const dir = new THREE.Vector3().subVectors(center, this.currentCamera.position).normalize();
    this.setOrientationFromDirection(dir);
    this.currentCamera.lookAt(center);
    this.setFocusTarget(center, distance, radius);
  }

  getForward() {
    return new THREE.Vector3(0, 0, -1)
      .applyEuler(new THREE.Euler(this.pitch, this.yaw, 0, "YXZ"))
      .normalize();
  }

  getRight(forward = this.getForward()) {
    return new THREE.Vector3().crossVectors(forward, WORLD_UP).normalize();
  }

  getUp(forward = this.getForward()) {
    return new THREE.Vector3().crossVectors(this.getRight(forward), forward).normalize();
  }

  getSpeedMultiplier() {
    if (this.keys.shift) return this.moveSpeedMultiplier * 4;
    if (this.keys.alt) return this.moveSpeedMultiplier * 0.25;
    return this.moveSpeedMultiplier;
  }

  isSpeedWheelMode() {
    return (this.pressedButtons & 2) !== 0;
  }

  panByScreenDelta(dx, dy, distance) {
    if (!this.currentCamera) {
      return;
    }

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

  orbitByScreenDelta(dx, dy) {
    if (!this.currentCamera) {
      return;
    }

    this.yaw -= dx * this.orbitSensitivity;
    this.pitch = THREE.MathUtils.clamp(this.pitch - dy * this.orbitSensitivity, MIN_PITCH, MAX_PITCH);

    const forward = this.getForward();
    this.currentCamera.position.copy(this.pivot).addScaledVector(forward, -this.pivotRadius);
  }

  isNavigationPointer(event) {
    return this.isRightButton(event) || this.isMiddleButton(event) || (this.isLeftButton(event) && event.altKey);
  }

  isLeftButton(event) {
    return (event.buttons & 1) !== 0;
  }

  isRightButton(event) {
    return (event.buttons & 2) !== 0;
  }

  isMiddleButton(event) {
    return (event.buttons & 4) !== 0;
  }

  markMoved() {
    this.onUserMove?.();
  }
}
