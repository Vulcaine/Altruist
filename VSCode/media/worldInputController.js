import * as THREE from "./vendor/three.module.js";

const MIN_POLAR = 0.15;
const MAX_POLAR = Math.PI - 0.15;

export class WorldInputController {
  constructor() {
    this.domElement = null;
    this.dragMode = null;
    this.pointerId = null;
    this.lastX = 0;
    this.lastY = 0;
    this.radius = 120;
    this.theta = Math.PI / 4;
    this.phi = 1.0;
    this.target = new THREE.Vector3(0, 0, 0);

    this.handlePointerDown = this.handlePointerDown.bind(this);
    this.handlePointerMove = this.handlePointerMove.bind(this);
    this.handlePointerUp = this.handlePointerUp.bind(this);
    this.handleWheel = this.handleWheel.bind(this);
    this.handleContextMenu = this.handleContextMenu.bind(this);
  }

  attach(domElement) {
    this.detach();
    this.domElement = domElement;
    domElement.addEventListener("pointerdown", this.handlePointerDown);
    domElement.addEventListener("pointermove", this.handlePointerMove);
    domElement.addEventListener("pointerup", this.handlePointerUp);
    domElement.addEventListener("pointerleave", this.handlePointerUp);
    domElement.addEventListener("wheel", this.handleWheel, { passive: false });
    domElement.addEventListener("contextmenu", this.handleContextMenu);
  }

  detach() {
    if (!this.domElement) {
      return;
    }

    this.domElement.removeEventListener("pointerdown", this.handlePointerDown);
    this.domElement.removeEventListener("pointermove", this.handlePointerMove);
    this.domElement.removeEventListener("pointerup", this.handlePointerUp);
    this.domElement.removeEventListener("pointerleave", this.handlePointerUp);
    this.domElement.removeEventListener("wheel", this.handleWheel);
    this.domElement.removeEventListener("contextmenu", this.handleContextMenu);
    this.domElement = null;
    this.dragMode = null;
    this.pointerId = null;
  }

  updateCamera(camera) {
    const sinPhi = Math.sin(this.phi);
    const x = this.target.x + this.radius * sinPhi * Math.sin(this.theta);
    const y = this.target.y + this.radius * Math.cos(this.phi);
    const z = this.target.z + this.radius * sinPhi * Math.cos(this.theta);

    camera.position.set(x, y, z);
    camera.lookAt(this.target);
  }

  focus(bounds) {
    const center = bounds.center || new THREE.Vector3();
    const radius = Math.max(4, Number(bounds.radius || 8));

    this.target.copy(center);
    this.radius = THREE.MathUtils.clamp(radius * 2.3, 6, 5000);
    this.phi = THREE.MathUtils.clamp(this.phi, MIN_POLAR, MAX_POLAR);
  }

  setTarget(target) {
    this.target.copy(target);
  }

  handlePointerDown(event) {
    if (!this.domElement) {
      return;
    }

    this.pointerId = event.pointerId;
    this.lastX = event.clientX;
    this.lastY = event.clientY;

    if (event.button === 2 || event.button === 1) {
      this.dragMode = "pan";
    } else {
      this.dragMode = "orbit";
    }

    this.domElement.setPointerCapture?.(event.pointerId);
  }

  handlePointerMove(event) {
    if (!this.dragMode || event.pointerId !== this.pointerId) {
      return;
    }

    const dx = event.clientX - this.lastX;
    const dy = event.clientY - this.lastY;
    this.lastX = event.clientX;
    this.lastY = event.clientY;

    if (this.dragMode === "orbit") {
      this.theta -= dx * 0.008;
      this.phi = THREE.MathUtils.clamp(this.phi + dy * 0.008, MIN_POLAR, MAX_POLAR);
      return;
    }

    const panSpeed = Math.max(0.05, this.radius * 0.0018);
    const forward = new THREE.Vector3(
      Math.sin(this.phi) * Math.sin(this.theta),
      Math.cos(this.phi),
      Math.sin(this.phi) * Math.cos(this.theta)
    ).normalize();
    const right = new THREE.Vector3().crossVectors(forward, new THREE.Vector3(0, 1, 0)).normalize();
    const up = new THREE.Vector3().crossVectors(right, forward).normalize();

    this.target.addScaledVector(right, -dx * panSpeed);
    this.target.addScaledVector(up, dy * panSpeed);
  }

  handlePointerUp(event) {
    if (event.pointerId !== this.pointerId) {
      return;
    }

    this.dragMode = null;
    this.pointerId = null;
  }

  handleWheel(event) {
    event.preventDefault();
    const factor = Math.exp(event.deltaY * 0.001);
    this.radius = THREE.MathUtils.clamp(this.radius * factor, 3, 10000);
  }

  handleContextMenu(event) {
    event.preventDefault();
  }
}
