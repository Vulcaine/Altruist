import * as THREE from "./vendor/three.module.js";

const SHAPE_SPHERE = 0;
const SHAPE_BOX = 1;
const SHAPE_CAPSULE = 2;
const SHAPE_HEIGHTFIELD = 3;

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
    this.grid = null;
    this.objects = new Map();
    this.pickables = [];
    this.selectedId = null;
    this.selectionHelper = null;
    this.worldSnapshot = null;
  }

  mount(container) {
    this.dispose();

    this.container = container;
    this.viewport = container;

    this.scene = new THREE.Scene();
    this.scene.background = new THREE.Color(0x06080c);
    this.scene.fog = new THREE.Fog(0x06080c, 600, 6000);

    this.camera = new THREE.PerspectiveCamera(60, 1, 0.1, 30000);

    this.renderer = new THREE.WebGLRenderer({
      antialias: true,
      alpha: false,
    });
    this.renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
    this.renderer.outputColorSpace = THREE.SRGBColorSpace;

    this.objectLayer = new THREE.Group();
    this.overlayLayer = new THREE.Group();

    const ambient = new THREE.AmbientLight(0xffffff, 0.55);
    const directional = new THREE.DirectionalLight(0xfff3d4, 0.8);
    directional.position.set(180, 320, 140);

    this.grid = new THREE.GridHelper(3000, 120, 0x6b7280, 0x20252d);
    this.grid.position.y = 0;

    this.scene.add(ambient, directional, this.grid, this.objectLayer, this.overlayLayer);

    container.innerHTML = "";
    container.appendChild(this.renderer.domElement);
    this.inputController.attach(this.renderer.domElement);

    this.resizeObserver = new ResizeObserver(() => this.resize());
    this.resizeObserver.observe(container);
    this.resize();
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
    this.grid = null;
    this.objects.clear();
    this.pickables = [];
    this.selectedId = null;
    this.selectionHelper = null;
    this.worldSnapshot = null;
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
  }

  startLoop() {
    const tick = () => {
      if (this.camera && this.renderer && this.scene) {
        this.inputController.updateCamera(this.camera);
        this.renderer.render(this.scene, this.camera);
      }

      this.frameHandle = requestAnimationFrame(tick);
    };

    this.frameHandle = requestAnimationFrame(tick);
  }

  setSnapshot(snapshot) {
    this.worldSnapshot = snapshot || null;
    this.rebuildWorld();
  }

  getObjects() {
    return Array.from(this.objects.values()).map((entry) => entry.dto);
  }

  getObject(instanceId) {
    return this.objects.get(instanceId)?.dto || null;
  }

  setSelectedObject(instanceId) {
    this.selectedId = instanceId || null;
    this.refreshSelectionHelper();
  }

  focusOnObject(instanceId) {
    const entry = this.objects.get(instanceId);
    if (!entry) {
      return;
    }

    const box = new THREE.Box3().setFromObject(entry.root);
    const size = box.getSize(new THREE.Vector3());
    const center = box.getCenter(new THREE.Vector3());
    const radius = Math.max(4, size.length() * 0.5, Math.max(size.x, size.y, size.z) * 0.7);
    this.inputController.focus({ center, radius });
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
    const updates = flattenRealtimeObjects(packet);
    if (!updates.length) {
      return;
    }

    for (const update of updates) {
      const id = update.instanceId || update.id;
      if (!id) {
        continue;
      }

      const entry = this.objects.get(id);
      if (entry) {
        entry.root.position.set(update.position.x, update.position.y, update.position.z);
        entry.dto.transform.position = { ...update.position };
        continue;
      }

      const placeholder = {
        instanceId: id,
        archetype: update.archetype || "RuntimeObject",
        zoneId: "",
        clientId: "",
        expired: false,
        transform: {
          position: { ...update.position },
          size: { x: 0.7, y: 1.4, z: 0.7 },
          scale: { x: 1, y: 1, z: 1 },
        },
        colliders: [],
      };

      this.addObjectToScene(placeholder);
    }

    this.refreshSelectionHelper();
  }

  rebuildWorld() {
    if (!this.objectLayer || !this.overlayLayer) {
      return;
    }

    clearGroup(this.objectLayer);
    clearGroup(this.overlayLayer);
    this.objects.clear();
    this.pickables = [];
    this.selectionHelper = null;

    const partitions = this.worldSnapshot?.partitions || [];
    for (const partition of partitions) {
      const partitionEntries = [];

      for (const object of partition.objects || []) {
        const entry = this.addObjectToScene(object);
        if (entry) {
          partitionEntries.push(entry);
        }
      }

      if (partitionEntries.length > 0) {
        this.overlayLayer.add(buildPartitionOverlay(partitionEntries));
      }
    }

    if (this.objects.size > 0) {
      const initialId = this.selectedId && this.objects.has(this.selectedId)
        ? this.selectedId
        : this.getObjects()[0].instanceId;
      this.selectedId = initialId;
      this.focusOnObject(initialId);
      this.refreshSelectionHelper();
    }
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

    if (colliders.length > 0) {
      for (const collider of colliders) {
        const mesh = buildColliderMesh(object, collider);
        if (!mesh) {
          continue;
        }

        mesh.userData.instanceId = object.instanceId;
        root.add(mesh);
        pickables.push(mesh);
      }
    } else {
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
    }

    if (!this.selectedId) {
      return;
    }

    const entry = this.objects.get(this.selectedId);
    if (!entry) {
      return;
    }

    const helper = new THREE.BoxHelper(entry.root, 0xd08c0e);
    this.selectionHelper = helper;
    this.overlayLayer.add(helper);
  }
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

function buildColliderMesh(object, collider) {
  const objectPosition = object.transform?.position || { x: 0, y: 0, z: 0 };
  const transform = collider.transform || object.transform || {};
  const position = transform.position || objectPosition;
  const size = transform.size || object.transform?.size || { x: 1, y: 1, z: 1 };
  const scale = transform.scale || object.transform?.scale || { x: 1, y: 1, z: 1 };
  const localPosition = new THREE.Vector3(
    position.x - objectPosition.x,
    position.y - objectPosition.y,
    position.z - objectPosition.z
  );

  if (collider.shape === SHAPE_HEIGHTFIELD || collider.heightfield) {
    const mesh = buildHeightfieldMesh(collider.heightfield);
    mesh.position.copy(localPosition);
    return mesh;
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

function buildHeightfieldMesh(heightfield) {
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

  const surface = new THREE.Mesh(
    geometry,
    new THREE.MeshBasicMaterial({
      color: 0x38bdf8,
      transparent: true,
      opacity: 0.18,
      wireframe: false,
    })
  );
  const wire = new THREE.LineSegments(
    new THREE.EdgesGeometry(geometry),
    new THREE.LineBasicMaterial({ color: 0x7dd3fc })
  );
  surface.add(wire);
  return surface;
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

function buildPartitionOverlay(entries) {
  const box = new THREE.Box3();
  for (const entry of entries) {
    box.union(new THREE.Box3().setFromObject(entry.root));
  }

  const size = box.getSize(new THREE.Vector3());
  const center = box.getCenter(new THREE.Vector3());
  const geometry = new THREE.BoxGeometry(
    Math.max(1, size.x + 2),
    Math.max(1, size.y + 2),
    Math.max(1, size.z + 2)
  );
  const edges = new THREE.LineSegments(
    new THREE.EdgesGeometry(geometry),
    new THREE.LineBasicMaterial({
      color: 0xd08c0e,
      transparent: true,
      opacity: 0.22,
    })
  );

  edges.position.copy(center);
  return edges;
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
