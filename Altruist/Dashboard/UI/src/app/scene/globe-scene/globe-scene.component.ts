import { isPlatformBrowser } from '@angular/common';
import {
  AfterViewInit,
  Component,
  ElementRef,
  Inject,
  NgZone,
  OnDestroy,
  PLATFORM_ID,
  ViewChild,
} from '@angular/core';

import * as THREE from 'three';
import { OrbitControls } from 'three/examples/jsm/controls/OrbitControls';

@Component({
  selector: 'app-globe-scene',
  templateUrl: './globe-scene.component.html',
  styleUrls: ['./globe-scene.component.scss'],
})
export class GlobeSceneComponent implements AfterViewInit, OnDestroy {
  @ViewChild('container', { static: true })
  private containerRef!: ElementRef<HTMLDivElement>;

  private scene!: THREE.Scene;
  private camera!: THREE.PerspectiveCamera;
  private renderer!: THREE.WebGLRenderer;
  private controls!: OrbitControls;
  private animationFrameId?: number;

  private readonly isBrowser: boolean;

  constructor(private zone: NgZone, @Inject(PLATFORM_ID) platformId: Object) {
    this.isBrowser = isPlatformBrowser(platformId);
  }

  ngAfterViewInit(): void {
    if (!this.isBrowser) {
      // SSR: do nothing, no Three.js initialization
      return;
    }

    this.initScene();
    this.initRenderer();
    this.initCamera();
    this.initControls();
    this.addGlobe();
    this.addLights();

    this.onResize();
    window.addEventListener('resize', this.onResize);

    // Run animation loop outside Angular to avoid change detection spam
    this.zone.runOutsideAngular(() => {
      this.animate();
    });
  }

  ngOnDestroy(): void {
    if (!this.isBrowser) {
      return;
    }

    window.removeEventListener('resize', this.onResize);

    if (this.animationFrameId != null) {
      cancelAnimationFrame(this.animationFrameId);
    }
    if (this.renderer) {
      this.renderer.dispose();
    }
  }

  private initScene(): void {
    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color(0x020617);
  }

  private initRenderer(): void {
    const container = this.containerRef.nativeElement;

    this.renderer = new THREE.WebGLRenderer({
      antialias: true,
    });
    this.renderer.setPixelRatio(window.devicePixelRatio);
    this.renderer.setSize(container.clientWidth, container.clientHeight);
    container.appendChild(this.renderer.domElement);
  }

  private initCamera(): void {
    const container = this.containerRef.nativeElement;
    const aspect = container.clientWidth / Math.max(container.clientHeight, 1);

    this.camera = new THREE.PerspectiveCamera(45, aspect, 0.1, 1000);
    this.camera.position.set(0, 0, 6);
  }

  private initControls(): void {
    this.controls = new OrbitControls(this.camera, this.renderer.domElement);
    this.controls.enableDamping = true;
    this.controls.dampingFactor = 0.05;
    this.controls.rotateSpeed = 0.6;
    this.controls.enablePan = false;
    this.controls.minDistance = 3;
    this.controls.maxDistance = 12;
  }

  private addGlobe(): void {
    const radius = 2;
    const widthSegments = 32;
    const heightSegments = 18;

    // Main wireframe sphere
    const sphereGeo = new THREE.SphereGeometry(
      radius,
      widthSegments,
      heightSegments
    );
    const wireMat = new THREE.MeshBasicMaterial({
      color: 0x38bdf8,
      wireframe: true,
      transparent: true,
      opacity: 0.8,
    });
    const globe = new THREE.Mesh(sphereGeo, wireMat);
    this.scene.add(globe);

    // Longitudinal & latitudinal rings (network globe vibe)
    const ringMat = new THREE.LineBasicMaterial({
      color: 0x4f46e5,
      transparent: true,
      opacity: 0.4,
    });

    const ringCount = 6;
    for (let i = 0; i < ringCount; i++) {
      // Latitude rings
      const latGeo = new THREE.CircleGeometry(radius, 64);
      // @ts-ignore - vertices not present in newer three versions, safe no-op
      latGeo.vertices?.shift?.();

      const lat = new THREE.LineLoop(latGeo, ringMat);
      const latAngle = ((i + 1) / (ringCount + 1)) * Math.PI - Math.PI / 2;
      lat.rotation.x = Math.PI / 2;
      lat.position.y = radius * Math.sin(latAngle);
      const projectedRadius = radius * Math.cos(latAngle);
      lat.scale.set(projectedRadius / radius, projectedRadius / radius, 1);
      this.scene.add(lat);

      // Longitude rings
      const longGeo = new THREE.CircleGeometry(radius, 64);
      const lon = new THREE.LineLoop(longGeo, ringMat);
      lon.rotation.y = (i / ringCount) * Math.PI;
      this.scene.add(lon);
    }

    this.scene.userData['globe'] = globe;
  }

  private addLights(): void {
    const ambient = new THREE.AmbientLight(0xffffff, 0.3);
    this.scene.add(ambient);

    const dir = new THREE.DirectionalLight(0xffffff, 0.8);
    dir.position.set(5, 5, 5);
    this.scene.add(dir);
  }

  private onResize = () => {
    if (!this.isBrowser || !this.renderer || !this.camera) return;

    const container = this.containerRef.nativeElement;
    const width = container.clientWidth;
    const height = container.clientHeight || 1;

    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();

    this.renderer.setSize(width, height);
  };

  private animate = () => {
    if (!this.isBrowser) return;

    this.animationFrameId = requestAnimationFrame(this.animate);

    const globe = this.scene.userData['globe'] as THREE.Mesh | undefined;
    if (globe) {
      globe.rotation.y += 0.0025;
    }

    this.controls.update();
    this.renderer.render(this.scene, this.camera);
  };
}
