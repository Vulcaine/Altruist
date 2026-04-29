import * as THREE from "./vendor/three.module.js";

const SHAPE_SPHERE = 0;
const SHAPE_BOX = 1;
const SHAPE_CAPSULE = 2;
const SHAPE_HEIGHTFIELD = 3;
const WORLD_UP = new THREE.Vector3(0, 1, 0);
const TRIGGER_COLLIDER_RENDER_RADIUS_LIMIT = 64;

export class WorldRenderer {
  constructor(inputController) {
    this.inputController = inputController;
    this.scene = null;
    this.camera = null;
    this.renderer = null;
    this.container = null;
    this.viewport = null;
    this.raycaster = new THREE.Raycaster();
    this.pointer = new THREE.Vector2();
    this.frameHandle = null;
    this.resizeObserver = null;
    this.objectLayer = null;
    this.overlayLayer = null;
    this.gizmoLayer = null;
    this.grid = null;
    this.objects = new Map();
    this.gizmos = new Map();
    this.hiddenGizmoIds = new Set();
    this.hiddenGizmoCategories = new Set();
    this.visiblePartitionKeys = new Set();
    this.hiddenObjectIds = new Set();
    this.pickables = [];
    this.selectedId = null;
    this.selectionHelper = null;
    this.worldSnapshot = null;
    this.renderScale = 1;
    this.lastFrameTime = performance.now();
    this.lastRenderTime = 0;
    this.lastStatsTime = performance.now();
    this.frameCounter = 0;
    this.dirty = true;
    this.activeFps = 45;
    this.idleFps = 5;
    this.showTerrainSurface = false;
    this.onStatsChanged = null;
    this.lastStats = {
      fps: 0,
      drawCalls: 0,
      triangles: 0,
      geometries: 0,
      textures: 0,
    };
    this.cameraNear = 1;
    this.cameraFar = 30000;
  }

  mount(container) {
    this.dispose();

    this.container = container;
    this.viewport = container;

    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color(0x06080c);
    this.scene.fog = new THREE.Fog(0x06080c, this.cameraFar * 0.2, this.cameraFar);

    this.camera = new THREE.PerspectiveCamera(60, 1, this.cameraNear, this.cameraFar);

    this.renderer = new THREE.WebGLRenderer({
      antialias: true,
      alpha: false,
    });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;

    this.objectLayer = new THREE.Group();
    this.overlayLayer = new THREE.Group();
    this.gizmoLayer = new THREE.Group();

    const ambient = new THREE.AmbientLight(0xffffff, 0.55);
    const directional = new THREE.DirectionalLight(0xfff3d4, 0.8);
    directional.position.set(180, 320, 140);

    this.grid = new THREE.Group();

    this.scene.add(ambient, directional, this.grid, this.objectLayer, this.gizmoLayer, this.overlayLayer);

    container.innerHTML = "";
    container.appendChild(this.renderer.domElement);
    this.inputController.attach(this.renderer.domElement);
    this.inputController.requestFocus = (center, radius) => this.focusCameraOnBounds(center, radius);
    this.inputController.onUserMove = () => this.markDirty();

    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(container);
    this.resize();
    this.lastFrameTime = performance.now();
    this.lastRenderTime = 0;
    this.markDirty();
    this.startLoop();
  }

  dispose() {
    if (this.frameHandle) {
      cancelAnimationFrame(this.frameHandle);
      this.frameHandle = null;
    }

    if (this.resizeObserver) {
      this.resizeObserver.disconnect();
      this.resizeObserver = null;
    }

    this.inputController.detach();

    if (this.renderer) {
      this.renderer.dispose();
    }

    if (this.container) {
      this.container.innerHTML = "";
    }

    this.scene = null;
    this.camera = null;
    this.renderer = null;
    this.objectLayer = null;
    this.overlayLayer = null;
    this.gizmoLayer = null;
    this.grid = null;
    this.objects.clear();
    this.gizmos.clear();
    this.pickables = [];
    this.selectedId = null;
    this.selectionHelper = null;
    this.worldSnapshot = null;
    this.renderScale = 1;
    this.dirty = true;
  }

  resize() {
    if (!this.renderer || !this.camera || !this.container) {
      return;
    }

    const width = this.container.clientWidth || 1;
    const height = this.container.clientHeight || 1;
    this.camera.aspect = width / height;
    this.camera.updateProjectionMatrix();
    this.renderer.setSize(width, height);
    this.markDirty();
  }

  setCameraClip(near, far) {
    const nextNear = THREE.MathUtils.clamp(
      Number.isFinite(Number(near)) ? Number(near) : this.cameraNear,
      0.01,
      100000
    );
    const nextFar = THREE.MathUtils.clamp(
      Number.isFinite(Number(far)) ? Number(far) : this.cameraFar,
      nextNear + 1,
      10000000
    );

    this.cameraNear = nextNear;
    this.cameraFar = nextFar;

    if (!this.camera) {
      return;
    }

    this.camera.near = this.cameraNear;
    this.camera.far = this.cameraFar;
    this.camera.updateProjectionMatrix();

    if (this.scene?.fog) {
      this.scene.fog.near = Math.min(600, this.cameraFar * 0.2);
      this.scene.fog.far = this.cameraFar;
    }

    this.markDirty();
  }

  getCameraClip() {
    return {
      near: this.cameraNear,
      far: this.cameraFar,
    };
  }

  startLoop() {
    const tick = (now) => {
      if (this.camera && this.renderer && this.scene) {
        const dt = Math.min(0.1, (now - this.lastFrameTime) / 1000);
        this.lastFrameTime = now;
        const activeMotion = Boolean(this.inputController.hasActiveMotion?.());
        let cameraMoved = false;

        if (this.dirty || activeMotion) {
          cameraMoved = Boolean(this.inputController.updateCamera(this.camera, dt));
        }

        const targetFps = activeMotion || cameraMoved ? this.activeFps : this.idleFps;
        const minFrameInterval = 1000 / Math.max(1, targetFps);
        const shouldRender = this.dirty || activeMotion || (now - this.lastRenderTime) >= minFrameInterval;

        if (shouldRender) {
          this.renderer.render(this.scene, this.camera);
          this.lastRenderTime = now;
          this.dirty = false;
          this.recordFrame(now);
        }
      }

      this.frameHandle = requestAnimationFrame(tick);
    };

    this.frameHandle = requestAnimationFrame(tick);
  }

  setSnapshot(snapshot) {
    if (snapshot === this.worldSnapshot) {
      this.markDirty();
      return;
    }

    this.worldSnapshot = snapshot || null;
    this.renderScale = normalizeRenderScale(this.worldSnapshot?.renderOptions?.renderScale);
    this.applyRenderScale();
    this.rebuildWorld();
  }

  beginSnapshot(snapshot) {
    this.worldSnapshot = snapshot || null;
    this.renderScale = normalizeRenderScale(this.worldSnapshot?.renderOptions?.renderScale);

    if (!this.objectLayer || !this.overlayLayer || !this.gizmoLayer) {
      return;
    }

    clearGroup(this.objectLayer);
    clearGroup(this.overlayLayer);
    clearGroup(this.gizmoLayer);
    clearGroup(this.grid);
    this.objects.clear();
    this.gizmos.clear();
    this.pickables = [];
    this.selectionHelper = null;
    this.applyRenderScale();
    this.markDirty();
  }

  appendPartition(partition) {
    if (!this.objectLayer || !this.worldSnapshot || !partition) {
      return;
    }

    if (!this.worldSnapshot.partitions) {
      this.worldSnapshot.partitions = [];
    }

    if (!this.worldSnapshot.partitions.includes(partition)) {
      this.worldSnapshot.partitions.push(partition);
    }

    if (!this.isPartitionVisible(partition)) {
      this.markDirty();
      return;
    }

    let addedTerrain = false;
    for (const object of partition.objects || []) {
      if (!object?.instanceId || this.hiddenObjectIds.has(object.instanceId) || this.objects.has(object.instanceId)) {
        continue;
      }

      const entry = this.addObjectToScene(object);
      addedTerrain = addedTerrain || Boolean(entry && isTerrainObject(object));
    }

    if (addedTerrain) {
      this.rebuildPlayArea();
    }

    if (this.selectedId) {
      this.refreshSelectionHelper();
    }

    this.markDirty();
  }

  setGizmos(gizmos) {
    if (!this.gizmoLayer) {
      return;
    }

    clearGroup(this.gizmoLayer);
    this.gizmos.clear();

    if (this.worldSnapshot) {
      this.worldSnapshot.gizmos = Array.isArray(gizmos) ? gizmos : [];
    }

    const allGizmos = Array.isArray(gizmos) ? gizmos : [];
    const batchedWalkability = allGizmos.filter(isWalkabilitySolidRect);
    const regularGizmos = allGizmos.filter((gizmo) => !isWalkabilitySolidRect(gizmo));

    if (batchedWalkability.length > 0) {
      const terrainSampler = createTerrainHeightSampler(this.worldSnapshot);
      const root = buildWalkabilityBatch(batchedWalkability, terrainSampler);
      if (root) {
        const id = "__walkability_batch__";
        root.userData.gizmoId = id;
        this.gizmoLayer.add(root);
        this.gizmos.set(id, {
          dto: {
            id,
            category: "walkability",
            source: "server",
            type: "batched-solid-rect",
            label: `${batchedWalkability.length} walkability cells`,
          },
          root,
        });
      }
    }

    for (const gizmo of regularGizmos) {
      this.upsertGizmo(gizmo);
    }

    this.refreshGizmoVisibility();
    this.markDirty();
  }

  getObjects() {
    return Array.from(this.objects.values()).map((entry) => entry.dto);
  }

  getObject(instanceId) {
    return this.objects.get(instanceId)?.dto || null;
  }

  getGizmos() {
    return Array.from(this.gizmos.values()).map((entry) => entry.dto);
  }

  setGizmoVisibility(hiddenIds, hiddenCategories) {
    const nextHiddenIds = new Set(hiddenIds || []);
    const nextHiddenCategories = new Set(
      Array.from(hiddenCategories || []).map((value) => String(value).toLowerCase())
    );

    if (setsEqual(this.hiddenGizmoIds, nextHiddenIds) && setsEqual(this.hiddenGizmoCategories, nextHiddenCategories)) {
      return;
    }

    this.hiddenGizmoIds = nextHiddenIds;
    this.hiddenGizmoCategories = nextHiddenCategories;
    this.refreshGizmoVisibility();
  }

  setSceneVisibility(visiblePartitionKeys, hiddenObjectIds) {
    const nextVisiblePartitionKeys = new Set(
      Array.from(visiblePartitionKeys || []).map((value) => String(value))
    );
    const nextHiddenObjectIds = new Set(hiddenObjectIds || []);

    if (setsEqual(this.visiblePartitionKeys, nextVisiblePartitionKeys) && setsEqual(this.hiddenObjectIds, nextHiddenObjectIds)) {
      return;
    }

    this.visiblePartitionKeys = nextVisiblePartitionKeys;
    this.hiddenObjectIds = nextHiddenObjectIds;
    this.rebuildWorld();
  }

  setSelectedObject(instanceId) {
    const nextSelectedId = instanceId || null;
    if (this.selectedId === nextSelectedId) {
      return;
    }

    this.selectedId = nextSelectedId;
    this.refreshSelectionHelper();
  }

  focusOnObject(instanceId) {
    const entry = this.objects.get(instanceId);
    if (!entry) {
      return;
    }

    const box = new THREE.Box3().setFromObject(entry.root);
    this.focusCameraOnBox(box);
  }

  focusCameraOnBox(box) {
    if (!box || box.isEmpty()) {
      return;
    }

    const center = box.getCenter(new THREE.Vector3());
    const radius = Math.max(4, box.getBoundingSphere(new THREE.Sphere()).radius);
    const viewDir = this.getCurrentViewDirection();
    const distance = this.getCameraDistanceForBounds(box, radius, viewDir);
    this.positionCameraForFocus(center, radius, distance, viewDir);
  }

  focusCameraOnBounds(center, radius) {
    if (!this.camera) {
      this.inputController.setFocusTarget(center, radius, radius);
      return;
    }

    const safeRadius = Math.max(4, Number(radius || 8));
    const distance = this.getCameraDistanceForRadius(safeRadius);
    this.positionCameraForFocus(center, safeRadius, distance, new THREE.Vector3(1, 0.65, 1).normalize());
  }

  positionCameraForFocus(center, radius, distance, viewDir) {
    if (!this.camera) {
      this.inputController.setFocusTarget(center, distance, radius);
      return;
    }

    const safeDistance = Math.max(6, distance);
    this.ensureCameraCanSeeBounds(radius, safeDistance);

    this.camera.position.set(
      center.x + viewDir.x * safeDistance,
      center.y + viewDir.y * safeDistance,
      center.z + viewDir.z * safeDistance
    );

    const dir = new THREE.Vector3().subVectors(center, this.camera.position).normalize();
    this.inputController.setOrientationFromDirection(dir);
    this.camera.lookAt(center);
    this.inputController.setFocusTarget(center, safeDistance, radius);
    this.markDirty();
  }

  getCameraDistanceForBounds(box, radius, viewDir) {
    if (!this.camera) {
      return radius * 2.3;
    }

    const verticalFov = THREE.MathUtils.degToRad(this.camera.fov || 60);
    const horizontalFov = 2 * Math.atan(Math.tan(verticalFov / 2) * Math.max(0.01, this.camera.aspect || 1));
    const center = box.getCenter(new THREE.Vector3());
    const direction = viewDir.clone().normalize();
    const right = new THREE.Vector3().crossVectors(WORLD_UP, direction);
    if (right.lengthSq() < 0.0001) {
      right.set(1, 0, 0);
    } else {
      right.normalize();
    }
    const up = new THREE.Vector3().crossVectors(direction, right).normalize();
    let halfWidth = 0;
    let halfHeight = 0;
    let halfDepth = 0;

    for (const corner of getBoxCorners(box)) {
      const offset = corner.sub(center);
      halfWidth = Math.max(halfWidth, Math.abs(offset.dot(right)));
      halfHeight = Math.max(halfHeight, Math.abs(offset.dot(up)));
      halfDepth = Math.max(halfDepth, Math.abs(offset.dot(direction)));
    }

    const widthDistance = halfWidth / Math.max(0.001, Math.tan(horizontalFov / 2));
    const heightDistance = halfHeight / Math.max(0.001, Math.tan(verticalFov / 2));
    return Math.max(6, (Math.max(widthDistance, heightDistance) + halfDepth) * 1.12);
  }

  getCameraDistanceForRadius(radius) {
    if (!this.camera) {
      return Math.max(6, radius * 2.3);
    }

    const verticalFov = THREE.MathUtils.degToRad(this.camera.fov || 60);
    const horizontalFov = 2 * Math.atan(Math.tan(verticalFov / 2) * Math.max(0.01, this.camera.aspect || 1));
    const fitFov = Math.max(0.01, Math.min(verticalFov, horizontalFov));
    return Math.max(6, (radius / Math.sin(fitFov / 2)) * 1.12);
  }

  getCurrentViewDirection() {
    if (!this.camera) {
      return new THREE.Vector3(1, 0.65, 1).normalize();
    }

    const direction = new THREE.Vector3().subVectors(this.camera.position, this.inputController.pivot);
    if (direction.lengthSq() < 0.0001) {
      return new THREE.Vector3(1, 0.65, 1).normalize();
    }

    return direction.normalize();
  }

  setPerformanceOptions(options = {}) {
    const nextActiveFps = THREE.MathUtils.clamp(Number(options.activeFps || this.activeFps), 1, 120);
    const nextIdleFps = THREE.MathUtils.clamp(Number(options.idleFps || this.idleFps), 0.5, 60);
    const nextShowTerrainSurface = Boolean(options.showTerrainSurface);
    const shouldRebuild = this.showTerrainSurface !== nextShowTerrainSurface;

    this.activeFps = nextActiveFps;
    this.idleFps = nextIdleFps;
    this.showTerrainSurface = nextShowTerrainSurface;

    if (shouldRebuild) {
      this.rebuildWorld();
      return;
    }

    this.markDirty();
  }

  getPerformanceStats() {
    return { ...this.lastStats };
  }

  markDirty() {
    this.dirty = true;
  }

  recordFrame(now) {
    this.frameCounter += 1;
    const elapsed = now - this.lastStatsTime;
    if (elapsed < 500) {
      return;
    }

    const fps = (this.frameCounter * 1000) / elapsed;
    this.frameCounter = 0;
    this.lastStatsTime = now;
    this.lastStats = {
      fps,
      drawCalls: this.renderer?.info?.render?.calls || 0,
      triangles: this.renderer?.info?.render?.triangles || 0,
      geometries: this.renderer?.info?.memory?.geometries || 0,
      textures: this.renderer?.info?.memory?.textures || 0,
    };
    this.onStatsChanged?.(this.getPerformanceStats());
  }

  pick(clientX, clientY) {
    if (!this.camera || !this.renderer || this.pickables.length === 0) {
      return null;
    }

    const rect = this.renderer.domElement.getBoundingClientRect();
    this.pointer.x = ((clientX - rect.left) / rect.width) * 2 - 1;
    this.pointer.y = -((clientY - rect.top) / rect.height) * 2 + 1;
    this.raycaster.setFromCamera(this.pointer, this.camera);

    const hits = this.raycaster.intersectObjects(this.pickables, true);
    if (!hits.length) {
      return null;
    }

    const picked = hits[0].object;
    return picked.userData.instanceId || picked.parent?.userData?.instanceId || null;
  }

  applyRealtimePacket(packet) {
    for (const id of packet?.removedObjectIds || []) {
      this.removeObject(id);
    }

    const updates = flattenRealtimeObjects(packet);

    for (const update of updates) {
      const id = update.instanceId || update.id;
      if (!id) {
        continue;
      }

      const entry = this.objects.get(id);
      if (entry) {
        if (update.position) {
          entry.root.position.set(update.position.x, update.position.y, update.position.z);
          entry.dto.transform.position = { ...update.position };
        }
        if (typeof update.name === "string") {
          entry.dto.name = update.name;
        }
        if (typeof update.archetype === "string") {
          entry.dto.archetype = update.archetype;
        }
        this.markDirty();
        continue;
      }

      const snapshotObject = this.findSnapshotObject(id);
      const snapshotPartition = this.findSnapshotPartitionForObject(id);
      if (
        snapshotObject &&
        !this.hiddenObjectIds.has(id) &&
        (!snapshotPartition || this.isPartitionVisible(snapshotPartition))
      ) {
        this.addObjectToScene(snapshotObject);
        this.markDirty();
      }
    }

    this.applyGizmoRealtimePacket(packet);
    this.refreshSelectionHelper();
    this.markDirty();
  }

  removeObject(id) {
    const entry = this.objects.get(id);
    if (!entry) {
      return;
    }

    this.objects.delete(id);
    this.pickables = this.pickables.filter((pickable) => !entry.pickables.includes(pickable));
    this.objectLayer?.remove(entry.root);
    disposeObject(entry.root);

    if (this.selectedId === id) {
      this.selectedId = null;
      this.refreshSelectionHelper();
    }

    this.markDirty();
  }

  findSnapshotObject(id) {
    for (const partition of this.worldSnapshot?.partitions || []) {
      for (const object of partition.objects || []) {
        if (object?.instanceId === id) {
          return object;
        }
      }
    }

    return null;
  }

  findSnapshotPartitionForObject(id) {
    for (const partition of this.worldSnapshot?.partitions || []) {
      if ((partition.objects || []).some((object) => object?.instanceId === id)) {
        return partition;
      }
    }

    return null;
  }

  applyGizmoRealtimePacket(packet) {
    for (const id of packet?.removedGizmoIds || []) {
      this.removeGizmo(id);
    }

    for (const gizmo of packet?.gizmos || []) {
      this.upsertGizmo(gizmo);
    }

    this.refreshGizmoVisibility();
    this.markDirty();
  }

  rebuildWorld() {
    if (!this.objectLayer || !this.overlayLayer || !this.gizmoLayer) {
      return;
    }

    clearGroup(this.objectLayer);
    clearGroup(this.overlayLayer);
    clearGroup(this.gizmoLayer);
    clearGroup(this.grid);
    this.objects.clear();
    this.gizmos.clear();
    this.pickables = [];
    this.selectionHelper = null;
    this.applyRenderScale();

    const playArea = buildPlayArea(this.worldSnapshot);
    if (playArea) {
      this.grid.add(playArea);
    }

    const partitions = this.worldSnapshot?.partitions || [];
    for (const partition of partitions) {
      if (!this.isPartitionVisible(partition)) {
        continue;
      }

      const partitionEntries = [];

      for (const object of partition.objects || []) {
        if (!object?.instanceId || this.hiddenObjectIds.has(object.instanceId)) {
          continue;
        }

        const entry = this.objects.get(object.instanceId) || this.addObjectToScene(object);
        if (entry) {
          partitionEntries.push(entry);
        }
      }

      // Per-object selection already gives local focus. Avoid drawing a huge
      // partition/terrain 3D box over the scene; the play-area plane is the
      // canonical world boundary.
    }

    const gizmos = this.worldSnapshot?.gizmos || [];
    const batchedWalkability = gizmos.filter(isWalkabilitySolidRect);
    const regularGizmos = gizmos.filter((gizmo) => !isWalkabilitySolidRect(gizmo));

    if (batchedWalkability.length > 0) {
      const terrainSampler = createTerrainHeightSampler(this.worldSnapshot);
      const root = buildWalkabilityBatch(batchedWalkability, terrainSampler);
      if (root) {
        const id = "__walkability_batch__";
        root.userData.gizmoId = id;
        this.gizmoLayer.add(root);
        this.gizmos.set(id, {
          dto: {
            id,
            category: "walkability",
            source: "server",
            type: "batched-solid-rect",
            label: `${batchedWalkability.length} walkability cells`,
          },
          root,
        });
      }
    }

    for (const gizmo of regularGizmos) {
      this.upsertGizmo(gizmo);
    }

    this.refreshGizmoVisibility();
    this.markDirty();

    if (this.objects.size > 0) {
      const firstInspectable = this.getObjects().find((object) => !isTerrainObject(object));
      const initialId = this.selectedId && this.objects.has(this.selectedId)
        ? this.selectedId
        : firstInspectable?.instanceId || null;
      this.selectedId = initialId;
      if (initialId) {
        this.focusOnObject(initialId);
      } else {
        this.focusOnPlayArea();
      }
      this.refreshSelectionHelper();
    }

    this.markDirty();
  }

  rebuildPlayArea() {
    if (!this.grid) {
      return;
    }

    clearGroup(this.grid);
    const playArea = buildPlayArea(this.worldSnapshot);
    if (playArea) {
      this.grid.add(playArea);
    }
    this.markDirty();
  }

  isPartitionVisible(partition) {
    if (!this.visiblePartitionKeys.size) {
      return true;
    }

    return this.visiblePartitionKeys.has(getPartitionKey(partition));
  }

  upsertGizmo(gizmo) {
    if (!this.gizmoLayer || !gizmo?.id) {
      return;
    }

    this.removeGizmo(gizmo.id);

    const root = buildGizmoObject(gizmo);
    if (!root) {
      return;
    }

    root.userData.gizmoId = gizmo.id;
    this.gizmoLayer.add(root);
    this.gizmos.set(gizmo.id, {
      dto: cloneObjectDto(gizmo),
      root,
    });
  }

  removeGizmo(id) {
    const entry = this.gizmos.get(id);
    if (!entry) {
      return;
    }

    this.gizmos.delete(id);
    this.gizmoLayer?.remove(entry.root);
    disposeObject(entry.root);
  }

  refreshGizmoVisibility() {
    for (const [id, entry] of this.gizmos) {
      const category = String(entry.dto.category || "").toLowerCase();
      entry.root.visible = !this.hiddenGizmoIds.has(id)
        && !this.hiddenGizmoCategories.has(category);
    }
    this.markDirty();
  }

  addObjectToScene(object) {
    if (!this.objectLayer) {
      return null;
    }

    const root = new THREE.Group();
    root.name = object.instanceId;
    root.userData.instanceId = object.instanceId;
    root.position.set(
      object.transform?.position?.x || 0,
      object.transform?.position?.y || 0,
      object.transform?.position?.z || 0
    );

    const pickables = [];
    const colliders = object.colliders || [];

    let renderedCollider = false;
    if (colliders.length > 0) {
      for (const collider of colliders) {
        const mesh = buildColliderMesh(object, collider, {
          showTerrainSurface: this.showTerrainSurface,
        });
        if (!mesh) {
          continue;
        }

        mesh.userData.instanceId = object.instanceId;
        root.add(mesh);
        pickables.push(mesh);
        renderedCollider = true;
      }
    }

    if (!renderedCollider && !isTerrainObject(object)) {
      const placeholder = buildPlaceholderMesh(object);
      placeholder.userData.instanceId = object.instanceId;
      root.add(placeholder);
      pickables.push(placeholder);
    }

    const marker = buildArchetypeMarker(object);
    marker.userData.instanceId = object.instanceId;
    root.add(marker);
    pickables.push(marker);

    this.objectLayer.add(root);

    const entry = {
      dto: cloneObjectDto(object),
      root,
      pickables,
    };

    this.objects.set(object.instanceId, entry);
    this.pickables.push(...pickables);
    return entry;
  }

  refreshSelectionHelper() {
    if (!this.overlayLayer) {
      return;
    }

    if (this.selectionHelper) {
      this.overlayLayer.remove(this.selectionHelper);
      disposeObject(this.selectionHelper);
      this.selectionHelper = null;
      this.markDirty();
    }

    if (!this.selectedId) {
      return;
    }

    const entry = this.objects.get(this.selectedId);
    if (!entry) {
      return;
    }

    if (isTerrainObject(entry.dto)) {
      return;
    }

    const helper = new THREE.Group();
    helper.add(new THREE.BoxHelper(entry.root, 0xd08c0e));

    const box = new THREE.Box3().setFromObject(entry.root);
    if (!box.isEmpty()) {
      helper.add(buildSelectionAxes(box));
    }

    this.selectionHelper = helper;
    this.overlayLayer.add(helper);
    this.markDirty();
  }

  applyRenderScale() {
    const scale = normalizeRenderScale(this.renderScale);
    this.renderScale = scale;

    for (const layer of [this.objectLayer, this.overlayLayer, this.gizmoLayer]) {
      if (layer) {
        layer.scale.setScalar(scale);
      }
    }

    if (this.grid) {
      this.grid.scale.setScalar(scale);
    }
    this.markDirty();
  }

  ensureCameraCanSeeBounds(radius, distance) {
    const requiredFar = Math.max(this.cameraFar, distance + (radius * 2.5), 1000);
    if (requiredFar > this.cameraFar * 1.05) {
      this.cameraFar = Math.min(requiredFar, 10000000);
    }

    if (this.camera) {
      this.camera.near = this.cameraNear;
      this.camera.far = this.cameraFar;
      this.camera.updateProjectionMatrix();
    }

    if (this.scene?.fog) {
      this.scene.fog.near = Math.min(Math.max(600, this.cameraFar * 0.04), this.cameraFar * 0.35);
      this.scene.fog.far = this.cameraFar;
    }
  }

  focusOnPlayArea() {
    const bounds = computeWorldBounds(this.worldSnapshot);
    if (!bounds) {
      return;
    }

    this.focusCameraOnBounds(bounds.center, bounds.radius);
  }
}

function normalizeRenderScale(value) {
  const scale = Number(value);
  if (!Number.isFinite(scale) || scale <= 0) {
    return 1;
  }

  return THREE.MathUtils.clamp(scale, 0.01, 100);
}

function getBoxCorners(box) {
  return [
    new THREE.Vector3(box.min.x, box.min.y, box.min.z),
    new THREE.Vector3(box.min.x, box.min.y, box.max.z),
    new THREE.Vector3(box.min.x, box.max.y, box.min.z),
    new THREE.Vector3(box.min.x, box.max.y, box.max.z),
    new THREE.Vector3(box.max.x, box.min.y, box.min.z),
    new THREE.Vector3(box.max.x, box.min.y, box.max.z),
    new THREE.Vector3(box.max.x, box.max.y, box.min.z),
    new THREE.Vector3(box.max.x, box.max.y, box.max.z),
  ];
}

function buildSelectionAxes(box) {
  const center = box.getCenter(new THREE.Vector3());
  const size = box.getSize(new THREE.Vector3());
  const axisLength = Math.max(2, Math.min(12, Math.max(size.x, size.y, size.z) * 1.25));
  const positions = [
    center.x, center.y, center.z, center.x + axisLength, center.y, center.z,
    center.x, center.y, center.z, center.x, center.y + axisLength, center.z,
    center.x, center.y, center.z, center.x, center.y, center.z + axisLength,
  ];
  const colors = [
    1, 0.08, 0.08, 1, 0.08, 0.08,
    0.1, 0.9, 0.2, 0.1, 0.9, 0.2,
    0.15, 0.45, 1, 0.15, 0.45, 1,
  ];
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.Float32BufferAttribute(positions, 3));
  geometry.setAttribute("color", new THREE.Float32BufferAttribute(colors, 3));
  const axes = new THREE.LineSegments(
    geometry,
    new THREE.LineBasicMaterial({
      vertexColors: true,
      depthTest: false,
      transparent: true,
      opacity: 0.95,
    })
  );
  axes.renderOrder = 250;
  return axes;
}

function flattenRealtimeObjects(packet) {
  if (!packet) {
    return [];
  }

  if (Array.isArray(packet.objects)) {
    return packet.objects;
  }

  if (Array.isArray(packet.partitions)) {
    return packet.partitions.flatMap((partition) => partition.objects || []);
  }

  return [];
}

function getPartitionKey(partition) {
  return `${partitionIndex(partition, "x")}:${partitionIndex(partition, "y")}:${partitionIndex(partition, "z")}`;
}

function partitionIndex(partition, axis) {
  const upper = `index${axis.toUpperCase()}`;
  return Number(partition?.[upper] ?? partition?.[axis] ?? 0);
}

function buildColliderMesh(object, collider, options = {}) {
  const objectPosition = object.transform?.position || { x: 0, y: 0, z: 0 };
  const transform = collider.transform || object.transform || {};
  const position = transform.position || { x: 0, y: 0, z: 0 };
  const size = transform.size || object.transform?.size || { x: 1, y: 1, z: 1 };
  const scale = transform.scale || object.transform?.scale || { x: 1, y: 1, z: 1 };
  const hasHeightfield = collider.shape === SHAPE_HEIGHTFIELD || collider.heightfield;
  const localPosition = getColliderLocalPosition(collider, position, objectPosition, hasHeightfield, size, scale);

  if (hasHeightfield) {
    const mesh = buildHeightfieldMesh(collider.heightfield, options);
    mesh.position.copy(localPosition);
    return mesh;
  }

  const triggerRadius = getColliderApproxRadius(collider, size, scale);
  if (collider.isTrigger && triggerRadius > TRIGGER_COLLIDER_RENDER_RADIUS_LIMIT) {
    return null;
  }

  const palette = getArchetypePalette(object.archetype);
  const material = new THREE.MeshStandardMaterial({
    color: palette.fill,
    emissive: palette.emissive,
    emissiveIntensity: 0.18,
    transparent: true,
    opacity: collider.isTrigger ? 0.22 : 0.32,
    wireframe: false,
  });

  let geometry;
  switch (collider.shape) {
    case SHAPE_SPHERE:
      geometry = new THREE.SphereGeometry(Math.max(0.35, size.x * scale.x), 18, 14);
      break;
    case SHAPE_CAPSULE: {
      const radius = Math.max(0.25, size.x * scale.x);
      const height = Math.max(0.25, size.y * scale.y * 2);
      geometry = new THREE.CapsuleGeometry(radius, height, 6, 10);
      break;
    }
    case SHAPE_BOX:
    default:
      geometry = new THREE.BoxGeometry(
        Math.max(0.4, size.x * scale.x * 2),
        Math.max(0.4, size.y * scale.y * 2),
        Math.max(0.4, size.z * scale.z * 2)
      );
      break;
  }

  const mesh = new THREE.Mesh(geometry, material);
  mesh.position.copy(localPosition);

  const edges = new THREE.LineSegments(
    new THREE.EdgesGeometry(geometry),
    new THREE.LineBasicMaterial({ color: palette.outline })
  );
  mesh.add(edges);
  return mesh;
}

function getColliderLocalPosition(collider, position, objectPosition, hasHeightfield, size, scale) {
  const pos = {
    x: Number(position?.x || 0),
    y: Number(position?.y || 0),
    z: Number(position?.z || 0),
  };
  const obj = {
    x: Number(objectPosition?.x || 0),
    y: Number(objectPosition?.y || 0),
    z: Number(objectPosition?.z || 0),
  };

  if (String(collider?.transformSpace || "").toLowerCase() === "world") {
    return new THREE.Vector3(pos.x - obj.x, pos.y - obj.y, pos.z - obj.z);
  }

  const isZero = Math.abs(pos.x) < 0.0001 && Math.abs(pos.y) < 0.0001 && Math.abs(pos.z) < 0.0001;
  const matchesObject = Math.abs(pos.x - obj.x) < 0.0001
    && Math.abs(pos.y - obj.y) < 0.0001
    && Math.abs(pos.z - obj.z) < 0.0001;

  const dx = pos.x - obj.x;
  const dy = pos.y - obj.y;
  const dz = pos.z - obj.z;
  const maxColliderExtent = Math.max(
    Math.abs(Number(size?.x || 0) * Number(scale?.x || 1)),
    Math.abs(Number(size?.y || 0) * Number(scale?.y || 1)),
    Math.abs(Number(size?.z || 0) * Number(scale?.z || 1)),
    1
  );
  const nearObject = ((dx * dx) + (dy * dy) + (dz * dz)) <= Math.max(16, maxColliderExtent * maxColliderExtent * 16);

  if (hasHeightfield || matchesObject || nearObject) {
    return new THREE.Vector3(pos.x - obj.x, pos.y - obj.y, pos.z - obj.z);
  }

  if (isZero) {
    return new THREE.Vector3(0, 0, 0);
  }

  return new THREE.Vector3(pos.x, pos.y, pos.z);
}

function getColliderApproxRadius(collider, size, scale) {
  const sx = Number(size?.x || 0) * Number(scale?.x || 1);
  const sy = Number(size?.y || 0) * Number(scale?.y || 1);
  const sz = Number(size?.z || 0) * Number(scale?.z || 1);

  if (collider.shape === SHAPE_SPHERE) {
    return Math.max(0, sx);
  }

  if (collider.shape === SHAPE_CAPSULE) {
    return Math.max(0, sx + sy);
  }

  return Math.max(Math.abs(sx), Math.abs(sy), Math.abs(sz));
}

function buildHeightfieldMesh(heightfield, options = {}) {
  if (!heightfield) {
    return new THREE.Mesh(
      new THREE.PlaneGeometry(1, 1),
      new THREE.MeshBasicMaterial({ wireframe: true, color: 0x38bdf8 })
    );
  }

  const geometry = new THREE.BufferGeometry();
  const width = Number(heightfield.width || 0);
  const height = Number(heightfield.height || 0);
  const cellSizeX = Number(heightfield.cellSizeX || 1);
  const cellSizeZ = Number(heightfield.cellSizeZ || 1);
  const positions = [];
  const indices = [];

  for (let z = 0; z < height; z++) {
    for (let x = 0; x < width; x++) {
      const value = Array.isArray(heightfield.heights?.[x])
        ? Number(heightfield.heights[x][z] || 0)
        : 0;
      positions.push(x * cellSizeX, value, z * cellSizeZ);
    }
  }

  for (let z = 0; z < height - 1; z++) {
    for (let x = 0; x < width - 1; x++) {
      const i00 = z * width + x;
      const i10 = z * width + x + 1;
      const i01 = (z + 1) * width + x;
      const i11 = (z + 1) * width + x + 1;
      indices.push(i00, i01, i10, i10, i01, i11);
    }
  }

  geometry.setAttribute("position", new THREE.Float32BufferAttribute(positions, 3));
  geometry.setIndex(indices);
  geometry.computeVertexNormals();

  const wire = buildHeightfieldWireframe(width, height, positions);
  if (!options.showTerrainSurface) {
    return wire;
  }

  const surface = new THREE.Mesh(
    geometry,
    new THREE.MeshBasicMaterial({
      color: 0x38bdf8,
      transparent: true,
      opacity: 0.18,
      wireframe: false,
    })
  );
  surface.add(wire);
  return surface;
}

function buildHeightfieldWireframe(width, height, positions) {
  const lines = [];

  const pushVertex = (index) => {
    const offset = index * 3;
    lines.push(positions[offset], positions[offset + 1], positions[offset + 2]);
  };

  for (let z = 0; z < height; z++) {
    for (let x = 0; x < width - 1; x++) {
      pushVertex((z * width) + x);
      pushVertex((z * width) + x + 1);
    }
  }

  for (let z = 0; z < height - 1; z++) {
    for (let x = 0; x < width; x++) {
      pushVertex((z * width) + x);
      pushVertex(((z + 1) * width) + x);
    }
  }

  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.Float32BufferAttribute(lines, 3));
  return new THREE.LineSegments(
    geometry,
    new THREE.LineBasicMaterial({
      color: 0x7dd3fc,
      transparent: true,
      opacity: 0.82,
    })
  );
}

function buildPlaceholderMesh(object) {
  const size = object.transform?.size || { x: 0.6, y: 1.2, z: 0.6 };
  const scale = object.transform?.scale || { x: 1, y: 1, z: 1 };
  const palette = getArchetypePalette(object.archetype);
  const geometry = new THREE.BoxGeometry(
    Math.max(0.4, size.x * scale.x * 2),
    Math.max(0.4, size.y * scale.y * 2),
    Math.max(0.4, size.z * scale.z * 2)
  );
  const material = new THREE.MeshStandardMaterial({
    color: palette.fill,
    emissive: palette.emissive,
    emissiveIntensity: 0.18,
    transparent: true,
    opacity: 0.4,
  });
  const mesh = new THREE.Mesh(geometry, material);
  const edges = new THREE.LineSegments(
    new THREE.EdgesGeometry(geometry),
    new THREE.LineBasicMaterial({ color: palette.outline })
  );
  mesh.add(edges);
  return mesh;
}

function buildArchetypeMarker(object) {
  const palette = getArchetypePalette(object.archetype);
  const geometry = new THREE.SphereGeometry(0.16, 10, 8);
  const material = new THREE.MeshBasicMaterial({ color: palette.marker });
  const mesh = new THREE.Mesh(geometry, material);
  mesh.position.set(0, Math.max(0.6, Number(object.transform?.size?.y || 1.2) * 1.4), 0);
  return mesh;
}

function buildPlayArea(snapshot) {
  const bounds = computeWorldBounds(snapshot);
  if (!bounds) {
    return null;
  }

  const group = new THREE.Group();
  const y = bounds.minY - 20;
  const plane = new THREE.Mesh(
    new THREE.PlaneGeometry(bounds.sizeX, bounds.sizeZ),
    new THREE.MeshBasicMaterial({
      color: 0xd08c0e,
      transparent: true,
      opacity: 0.035,
      depthWrite: false,
      side: THREE.DoubleSide,
    })
  );
  plane.rotation.x = -Math.PI / 2;
  plane.position.set(bounds.center.x, y, bounds.center.z);
  plane.renderOrder = -10;
  group.add(plane);

  group.add(buildPlayAreaGrid(bounds, y + 1));
  group.add(buildPlayAreaOutline(bounds, y + 2));
  return group;
}

function buildPlayAreaGrid(bounds, y) {
  const targetDivisions = 32;
  const step = Math.max(1, Math.max(bounds.sizeX, bounds.sizeZ) / targetDivisions);
  const lines = [];

  for (let x = bounds.minX; x <= bounds.maxX + 0.001; x += step) {
    lines.push(x, y, bounds.minZ, x, y, bounds.maxZ);
  }

  for (let z = bounds.minZ; z <= bounds.maxZ + 0.001; z += step) {
    lines.push(bounds.minX, y, z, bounds.maxX, y, z);
  }

  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.Float32BufferAttribute(lines, 3));
  return new THREE.LineSegments(
    geometry,
    new THREE.LineBasicMaterial({
      color: 0xd08c0e,
      transparent: true,
      opacity: 0.16,
    })
  );
}

function buildPlayAreaOutline(bounds, y) {
  const points = [
    new THREE.Vector3(bounds.minX, y, bounds.minZ),
    new THREE.Vector3(bounds.maxX, y, bounds.minZ),
    new THREE.Vector3(bounds.maxX, y, bounds.maxZ),
    new THREE.Vector3(bounds.minX, y, bounds.maxZ),
  ];

  return buildLine(points, new THREE.LineBasicMaterial({
    color: 0xd08c0e,
    transparent: true,
    opacity: 0.92,
  }), true);
}

function computeWorldBounds(snapshot) {
  const terrainObjects = (snapshot?.partitions || [])
    .flatMap((partition) => partition.objects || [])
    .filter(isTerrainObject);

  const objects = terrainObjects.length > 0
    ? terrainObjects
    : (snapshot?.partitions || []).flatMap((partition) => partition.objects || []);

  if (!objects.length) {
    return null;
  }

  let minX = Number.POSITIVE_INFINITY;
  let minY = Number.POSITIVE_INFINITY;
  let minZ = Number.POSITIVE_INFINITY;
  let maxX = Number.NEGATIVE_INFINITY;
  let maxY = Number.NEGATIVE_INFINITY;
  let maxZ = Number.NEGATIVE_INFINITY;

  for (const object of objects) {
    const position = object.transform?.position || { x: 0, y: 0, z: 0 };
    const size = object.transform?.size || { x: 1, y: 1, z: 1 };
    const objectMinX = Number(position.x || 0);
    const objectMinY = Number(position.y || 0);
    const objectMinZ = Number(position.z || 0);
    const objectMaxX = objectMinX + Math.max(1, Number(size.x || 1));
    const objectMaxY = objectMinY + Math.max(1, Number(size.y || 1));
    const objectMaxZ = objectMinZ + Math.max(1, Number(size.z || 1));

    minX = Math.min(minX, objectMinX);
    minY = Math.min(minY, objectMinY);
    minZ = Math.min(minZ, objectMinZ);
    maxX = Math.max(maxX, objectMaxX);
    maxY = Math.max(maxY, objectMaxY);
    maxZ = Math.max(maxZ, objectMaxZ);
  }

  const sizeX = Math.max(1, maxX - minX);
  const sizeY = Math.max(1, maxY - minY);
  const sizeZ = Math.max(1, maxZ - minZ);
  return {
    minX,
    minY,
    minZ,
    maxX,
    maxY,
    maxZ,
    sizeX,
    sizeY,
    sizeZ,
    center: new THREE.Vector3(minX + (sizeX * 0.5), minY + (sizeY * 0.5), minZ + (sizeZ * 0.5)),
    radius: Math.max(sizeX, sizeY, sizeZ) * 0.55,
  };
}

function isTerrainObject(object) {
  return (object?.colliders || []).some((collider) => collider?.heightfield);
}

function buildGizmoObject(gizmo) {
  const root = new THREE.Group();
  root.name = gizmo.id || "gizmo";

  const category = String(gizmo.category || "").toLowerCase();
  const color = parseColor(gizmo.color || "#38BDF8FF");
  const material = new THREE.LineBasicMaterial({
    color,
    transparent: true,
    opacity: parseAlpha(gizmo.color || "#38BDF8FF"),
  });
  const meshMaterial = new THREE.MeshBasicMaterial({
    color,
    transparent: true,
    opacity: Math.min(0.24, parseAlpha(gizmo.color || "#38BDF8FF")),
    wireframe: true,
  });

  const position = toVector3(gizmo.position);
  const type = String(gizmo.type || "sphere").toLowerCase();
  const points = (gizmo.points || []).map(toVector3);

  if (type === "polyline" && points.length >= 2) {
    root.add(buildLine(points, material, false));
    return root;
  }

  if ((type === "polygon" || type === "cone") && points.length >= 3) {
    root.add(buildLine(points, material, true));
    return root;
  }

  if (type === "circle") {
    root.add(buildCircle(position, Number(gizmo.radius || 1), material));
    return root;
  }

  if (type === "rect") {
    root.add(buildRect(position, Number(gizmo.width || 1), Number(gizmo.height || 1), material));
    return root;
  }

  if (type === "solid-rect" || type === "filled-rect") {
    root.add(buildSolidRect(
      position,
      Number(gizmo.width || 1),
      Number(gizmo.height || 1),
      color,
      parseAlpha(gizmo.color || "#38BDF8FF"),
      category
    ));
    return root;
  }

  if (type === "box") {
    const geometry = new THREE.BoxGeometry(
      Math.max(0.1, Number(gizmo.width || 1)),
      Math.max(0.1, Number(gizmo.height || 1)),
      Math.max(0.1, Number(gizmo.radius || gizmo.width || 1))
    );
    const edges = new THREE.LineSegments(new THREE.EdgesGeometry(geometry), material);
    edges.position.copy(position);
    root.add(edges);
    return root;
  }

  const sphere = new THREE.Mesh(
    new THREE.SphereGeometry(Math.max(0.05, Number(gizmo.radius || 0.35)), 18, 12),
    meshMaterial
  );
  sphere.position.copy(position);
  root.add(sphere);
  return root;
}

function buildLine(points, material, closed) {
  const linePoints = closed ? [...points, points[0]] : points;
  const geometry = new THREE.BufferGeometry().setFromPoints(linePoints);
  return new THREE.Line(geometry, material);
}

function buildCircle(center, radius, material) {
  const points = [];
  const segments = 64;
  for (let i = 0; i <= segments; i++) {
    const angle = (i / segments) * Math.PI * 2;
    points.push(new THREE.Vector3(
      center.x + Math.cos(angle) * radius,
      center.y,
      center.z + Math.sin(angle) * radius
    ));
  }
  return buildLine(points, material, false);
}

function buildRect(position, width, height, material) {
  const points = [
    new THREE.Vector3(position.x, position.y, position.z),
    new THREE.Vector3(position.x + width, position.y, position.z),
    new THREE.Vector3(position.x + width, position.y, position.z + height),
    new THREE.Vector3(position.x, position.y, position.z + height),
  ];
  return buildLine(points, material, true);
}

function buildSolidRect(position, width, height, color, alpha, category) {
  const geometry = new THREE.PlaneGeometry(Math.max(0.05, width), Math.max(0.05, height));
  const isWalkability = category === "walkability";
  const material = new THREE.MeshBasicMaterial({
    color,
    transparent: true,
    opacity: isWalkability ? 0.62 : Math.min(0.38, Math.max(0.08, alpha)),
    depthTest: false,
    depthWrite: false,
    side: THREE.DoubleSide,
  });
  const mesh = new THREE.Mesh(geometry, material);
  mesh.rotation.x = -Math.PI / 2;
  mesh.position.copy(position);
  if (isWalkability) {
    mesh.position.y += 0.35;
  }
  mesh.renderOrder = isWalkability ? 200 : 50;

  const root = new THREE.Group();
  root.add(mesh);

  if (isWalkability) {
    const outline = new THREE.LineSegments(
      new THREE.EdgesGeometry(geometry),
      new THREE.LineBasicMaterial({
        color,
        transparent: true,
        opacity: 0.95,
        depthTest: false,
      })
    );
    outline.rotation.x = -Math.PI / 2;
    outline.position.copy(mesh.position);
    outline.position.y += 0.01;
    outline.renderOrder = 201;
    root.add(outline);
  }

  return root;
}

function isWalkabilitySolidRect(gizmo) {
  const category = String(gizmo?.category || "").toLowerCase();
  const type = String(gizmo?.type || "").toLowerCase();
  return category === "walkability" && (type === "solid-rect" || type === "filled-rect");
}

function buildWalkabilityBatch(gizmos, terrainSampler) {
  const positions = [];
  const indices = [];
  const linePositions = [];
  let vertexOffset = 0;

  for (const gizmo of gizmos) {
    const center = toVector3(gizmo.position);
    const width = Math.max(0.05, Number(gizmo.width || 1));
    const height = Math.max(0.05, Number(gizmo.height || 1));
    const halfWidth = width * 0.5;
    const halfHeight = height * 0.5;
    const y00 = sampleOverlayHeight(terrainSampler, center.x - halfWidth, center.z - halfHeight, center.y);
    const y10 = sampleOverlayHeight(terrainSampler, center.x + halfWidth, center.z - halfHeight, center.y);
    const y11 = sampleOverlayHeight(terrainSampler, center.x + halfWidth, center.z + halfHeight, center.y);
    const y01 = sampleOverlayHeight(terrainSampler, center.x - halfWidth, center.z + halfHeight, center.y);

    if ([y00, y10, y11, y01].some((value) => value == null)) {
      continue;
    }

    const corners = [
      center.x - halfWidth, y00, center.z - halfHeight,
      center.x + halfWidth, y10, center.z - halfHeight,
      center.x + halfWidth, y11, center.z + halfHeight,
      center.x - halfWidth, y01, center.z + halfHeight,
    ];

    positions.push(...corners);
    indices.push(vertexOffset, vertexOffset + 1, vertexOffset + 2, vertexOffset, vertexOffset + 2, vertexOffset + 3);

    pushLineSegment(linePositions, corners, 0, 1);
    pushLineSegment(linePositions, corners, 1, 2);
    pushLineSegment(linePositions, corners, 2, 3);
    pushLineSegment(linePositions, corners, 3, 0);

    vertexOffset += 4;
  }

  if (positions.length === 0) {
    return null;
  }

  const color = parseColor(gizmos[0]?.color || "#FF2E2E77");
  const root = new THREE.Group();
  const geometry = new THREE.BufferGeometry();
  geometry.setAttribute("position", new THREE.Float32BufferAttribute(positions, 3));
  geometry.setIndex(indices);
  geometry.computeVertexNormals();

  const mesh = new THREE.Mesh(
    geometry,
    new THREE.MeshBasicMaterial({
      color,
      transparent: true,
      opacity: 0.48,
      depthTest: false,
      depthWrite: false,
      side: THREE.DoubleSide,
    })
  );
  mesh.renderOrder = 200;
  root.add(mesh);

  const lineGeometry = new THREE.BufferGeometry();
  lineGeometry.setAttribute("position", new THREE.Float32BufferAttribute(linePositions, 3));
  const outline = new THREE.LineSegments(
    lineGeometry,
    new THREE.LineBasicMaterial({
      color,
      transparent: true,
      opacity: 0.75,
      depthTest: false,
    })
  );
  outline.renderOrder = 201;
  root.add(outline);
  return root;
}

function createTerrainHeightSampler(snapshot) {
  const terrainObjects = (snapshot?.partitions || [])
    .flatMap((partition) => partition.objects || [])
    .filter((object) => (object.colliders || []).some((collider) => collider?.heightfield));

  if (terrainObjects.length === 0) {
    return null;
  }

  const terrains = terrainObjects
    .map((object) => {
      const collider = (object.colliders || []).find((item) => item?.heightfield);
      const heightfield = collider?.heightfield;
      if (!heightfield) {
        return null;
      }

      const objectPosition = object.transform?.position || { x: 0, y: 0, z: 0 };
      const colliderPosition = collider.transform?.position || objectPosition;
      return {
        originX: Number(colliderPosition.x || 0),
        originY: Number(colliderPosition.y || 0),
        originZ: Number(colliderPosition.z || 0),
        width: Number(heightfield.width || 0),
        height: Number(heightfield.height || 0),
        cellSizeX: Number(heightfield.cellSizeX || 1),
        cellSizeZ: Number(heightfield.cellSizeZ || 1),
        heights: heightfield.heights || [],
      };
    })
    .filter(Boolean);

  if (terrains.length === 0) {
    return null;
  }

  return {
    sample(x, z) {
      for (const terrain of terrains) {
        const localX = x - terrain.originX;
        const localZ = z - terrain.originZ;
        const maxX = (terrain.width - 1) * terrain.cellSizeX;
        const maxZ = (terrain.height - 1) * terrain.cellSizeZ;

        if (localX < 0 || localZ < 0 || localX > maxX || localZ > maxZ) {
          continue;
        }

        return terrain.originY + sampleHeightfieldBilinear(terrain, localX, localZ);
      }

      return null;
    },
  };
}

function sampleOverlayHeight(terrainSampler, x, z, fallbackY) {
  if (terrainSampler) {
    const sampled = terrainSampler.sample(x, z);
    return Number.isFinite(sampled) ? sampled + 8 : null;
  }

  return Number(fallbackY || 0) + 8;
}

function sampleHeightfieldBilinear(terrain, localX, localZ) {
  const fx = THREE.MathUtils.clamp(localX / terrain.cellSizeX, 0, Math.max(0, terrain.width - 1));
  const fz = THREE.MathUtils.clamp(localZ / terrain.cellSizeZ, 0, Math.max(0, terrain.height - 1));
  const x0 = Math.floor(fx);
  const z0 = Math.floor(fz);
  const x1 = Math.min(terrain.width - 1, x0 + 1);
  const z1 = Math.min(terrain.height - 1, z0 + 1);
  const tx = fx - x0;
  const tz = fz - z0;

  const h00 = getHeightfieldValue(terrain, x0, z0);
  const h10 = getHeightfieldValue(terrain, x1, z0);
  const h01 = getHeightfieldValue(terrain, x0, z1);
  const h11 = getHeightfieldValue(terrain, x1, z1);
  const hx0 = THREE.MathUtils.lerp(h00, h10, tx);
  const hx1 = THREE.MathUtils.lerp(h01, h11, tx);
  return THREE.MathUtils.lerp(hx0, hx1, tz);
}

function getHeightfieldValue(terrain, x, z) {
  const row = terrain.heights?.[x];
  if (!Array.isArray(row)) {
    return 0;
  }

  return Number(row[z] || 0);
}

function pushLineSegment(target, corners, from, to) {
  const fromOffset = from * 3;
  const toOffset = to * 3;
  target.push(
    corners[fromOffset],
    corners[fromOffset + 1],
    corners[fromOffset + 2],
    corners[toOffset],
    corners[toOffset + 1],
    corners[toOffset + 2]
  );
}

function toVector3(value) {
  return new THREE.Vector3(
    Number(value?.x ?? value?.X ?? 0),
    Number(value?.y ?? value?.Y ?? 0),
    Number(value?.z ?? value?.Z ?? 0)
  );
}

function parseColor(hex) {
  const normalized = normalizeHexColor(hex);
  return new THREE.Color(normalized.slice(0, 7));
}

function parseAlpha(hex) {
  const normalized = normalizeHexColor(hex);
  if (normalized.length < 9) {
    return 0.8;
  }
  return Math.max(0.05, Math.min(1, Number.parseInt(normalized.slice(7, 9), 16) / 255));
}

function normalizeHexColor(hex) {
  const value = String(hex || "#38BDF8FF").trim();
  if (/^#[0-9a-fA-F]{8}$/.test(value) || /^#[0-9a-fA-F]{6}$/.test(value)) {
    return value;
  }
  return "#38BDF8FF";
}

function getArchetypePalette(archetype) {
  const key = String(archetype || "Object");
  const hash = [...key].reduce((value, char) => value + char.charCodeAt(0), 0);
  const hue = hash % 360;
  const fill = new THREE.Color(`hsl(${hue} 60% 45%)`);
  const outline = new THREE.Color(`hsl(${hue} 72% 68%)`);
  const marker = new THREE.Color(`hsl(${(hue + 35) % 360} 90% 72%)`);
  return {
    fill,
    outline,
    marker,
    emissive: fill.clone().multiplyScalar(0.16),
  };
}

function cloneObjectDto(object) {
  return JSON.parse(JSON.stringify(object));
}

function setsEqual(left, right) {
  if (left.size !== right.size) {
    return false;
  }

  for (const value of left) {
    if (!right.has(value)) {
      return false;
    }
  }

  return true;
}

function clearGroup(group) {
  while (group.children.length > 0) {
    const child = group.children[0];
    disposeObject(child);
  }
}

function disposeObject(object) {
  object.traverse((child) => {
    child.geometry?.dispose?.();
    if (Array.isArray(child.material)) {
      child.material.forEach((material) => material.dispose?.());
    } else {
      child.material?.dispose?.();
    }
  });
  object.parent?.remove(object);
}
