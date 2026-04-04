declare module 'three/examples/jsm/controls/OrbitControls' {
  import { Camera, EventDispatcher } from 'three';

  export class OrbitControls extends EventDispatcher {
    constructor(object: Camera, domElement?: HTMLElement);

    enabled: boolean;
    enableDamping: boolean;
    dampingFactor: number;
    rotateSpeed: number;
    enablePan: boolean;
    minDistance: number;
    maxDistance: number;

    update(): void;
    dispose(): void;
  }
}
