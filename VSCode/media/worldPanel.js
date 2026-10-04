import { WorldInputController } from "./worldInputController.js";
import { WorldRenderer } from "./worldRenderer.js";

const vscode = acquireVsCodeApi();
const root = document.getElementById("world-root");
const DASHBOARD_WORLD_OBJECT_STATE_CODE = 13;

const state = {
  bootstrap: null,
  snapshot: null,
  error: "",
  selectedObjectId: null,
  selectedPartitionKey: null,
  filter: "",
  explorerTab: "objects",
  explorerCollapsed: false,
  explorerFullscreen: false,
  sceneControlsCollapsed: true,
  layersCollapsed: false,
  collapsedPartitionKeys: new Set(),
  visiblePartitionKeys: new Set(),
  hiddenObjectIds: new Set(),
  hiddenGizmoIds: new Set(),
  hiddenGizmoCategories: new Set(),
  websocketState: "idle",
  websocketError: "",
  reconnectTimer: null,
  shouldReconnect: false,
  streamState: "idle",
  streamError: "",
  streamLoadedPartitions: 0,
  streamRequestId: 0,
  streamRenderQueued: false,
  worldFullscreen: false,
  activeFps: 45,
  idleFps: 5,
  performanceMode: true,
  showTerrainSurface: false,
  perfStats: null,
};

const inputController = new WorldInputController();
const renderer = new WorldRenderer(inputController);
let worldSelect = null;
let objectSearch = null;
let objectList = null;
let gizmoList = null;
let layerList = null;
let inspector = null;
let viewport = null;
let cameraSpeedInput = null;
let cameraNearInput = null;
let cameraFarInput = null;
let activeFpsInput = null;
let idleFpsInput = null;
let performanceModeInput = null;
let terrainSurfaceInput = null;
let ws = null;

inputController.onSpeedChanged = () => {
  updateSpeedUi();
};

renderer.onStatsChanged = (stats) => {
  state.perfStats = stats;
  renderPerformanceHud();
};

window.addEventListener("message", (event) => {
  const message = event.data;

  switch (message?.type) {
    case "world:bootstrap":
      state.bootstrap = message.payload || null;
      state.snapshot = message.payload?.snapshot || null;
      state.error = "";
      reconcileSceneState();
      renderShell();
      refreshWorld();
      connectWebSocket();
      loadWorldSnapshotStream();
      return;
    case "world:snapshot":
      state.snapshot = message.payload?.snapshot || null;
      if (message.payload?.selectedWorldIndex != null) {
        state.bootstrap = {
          ...(state.bootstrap || {}),
          selectedWorldIndex: message.payload.selectedWorldIndex,
        };
      }
      reconcileSceneState();
      refreshWorld();
      loadWorldSnapshotStream();
      return;
    case "world:error":
      state.error = message.error || "Unknown world viewer error.";
      renderShell();
      refreshWorld();
      return;
    case "world:stream-partition":
      if (isCurrentStreamMessage(message)) {
        appendStreamPartition(message.partition);
      }
      return;
    case "world:stream-complete":
      if (isCurrentStreamMessage(message)) {
        state.snapshot.gizmos = Array.isArray(message.gizmos) ? message.gizmos : [];
        state.streamState = "ready";
        state.streamError = "";
        renderer.setGizmos(state.snapshot.gizmos);
        renderer.setGizmoVisibility(state.hiddenGizmoIds, state.hiddenGizmoCategories);
        finishStreamRender();
      }
      return;
    case "world:stream-error":
      if (isCurrentStreamMessage(message)) {
        state.streamState = "error";
        state.streamError = message.error || "World snapshot stream failed.";
        renderShell();
        refreshWorld();
      }
      return;
    default:
      return;
  }
});

window.addEventListener("beforeunload", () => {
  disconnectWebSocket();
  renderer.dispose();
});

window.addEventListener("keydown", (event) => {
  if (event.key !== "Escape") {
    return;
  }

  if (state.explorerFullscreen) {
    exitExplorerFullscreen();
    return;
  }

  if (state.worldFullscreen) {
    exitWorldFullscreen();
  }
});

document.addEventListener("fullscreenchange", () => {
  if (document.fullscreenElement) {
    return;
  }

  if (state.worldFullscreen) {
    state.worldFullscreen = false;
    applyWorldFullscreenState();
  }
});

vscode.postMessage({ type: "world:ready" });
renderShell();
refreshWorld();

function renderShell() {
  if (!state.bootstrap && !state.error) {
    root.innerHTML = `<div class="loading">Waiting for world bootstrap data...</div>`;
    return;
  }

  const environmentMode = resolveEnvironmentMode();
  const worlds = state.bootstrap?.worlds || [];
  const selectedWorldIndex = state.bootstrap?.selectedWorldIndex;
  const websocketChip = buildWebsocketChip();
  const worldCount = worlds.length;

  root.innerHTML = `
    <div class="world-shell ${state.worldFullscreen ? "is-fullscreen" : ""}">
      <section class="world-toolbar">
        <div class="toolbar-main">
          <div class="eyebrow">Altruist Dashboard</div>
          <div class="toolbar-title">
            <h1>World View</h1>
            <span class="chip"><strong>Environment</strong> ${escapeHtml(environmentMode)}</span>
          </div>
          <div class="toolbar-subtitle">Native VS Code 3D inspection panel backed by the live dashboard snapshot and websocket stream.</div>
        </div>
        <div class="toolbar-actions">
          <label class="chip world-picker">
            <strong>World</strong>
            <select id="world-select">
              ${worlds
                .map(
                  (world) => `
                    <option value="${world.index}" ${world.index === selectedWorldIndex ? "selected" : ""}>
                      ${escapeHtml(world.name || `World ${world.index}`)}
                    </option>
                  `
                )
                .join("")}
            </select>
          </label>
          <span class="chip"><strong>Worlds</strong> ${worldCount}</span>
          <button id="refresh-world" class="icon-button refresh-icon-button" title="Refresh" aria-label="Refresh">
            <span class="codicon codicon-refresh" aria-hidden="true"></span>
          </button>
        </div>
      </section>
      <section class="world-layout">
        <section class="editor-main">
        <section class="viewport-panel">
          <div class="scene-info-card ${state.sceneControlsCollapsed ? "is-collapsed" : ""}" data-collapsed="${state.sceneControlsCollapsed ? "true" : "false"}">
            <div class="scene-info-header">
              <div>
                <span class="eyebrow">Scene Controls</span>
                <span class="mini-badge">${escapeHtml(environmentMode)}</span>
              </div>
              <button id="scene-controls-toggle" class="ghost-button" aria-expanded="${state.sceneControlsCollapsed ? "false" : "true"}" title="${state.sceneControlsCollapsed ? "Expand scene controls" : "Collapse scene controls"}">
                ${state.sceneControlsCollapsed ? "Show" : "Hide"}
              </button>
            </div>
            <div class="scene-info-body">
              <div class="scene-info-stack">
              <div class="setting-row">
                <span>Stream</span>
                ${websocketChip}
              </div>
              <div class="setting-row">
                <span>Snapshot</span>
                <strong>${renderStreamSnapshotStatus()}</strong>
              </div>
              <div class="setting-row">
                <span>Objects</span>
                <strong id="object-count-chip">${getVisibleSnapshotObjects().length} visible / ${getUniqueSnapshotObjects().length} total</strong>
              </div>
              <div class="setting-row">
                <span>Render Scale</span>
                <strong>${formatRenderScale(state.snapshot?.renderOptions?.renderScale)}</strong>
              </div>
              <div class="setting-row">
                <span>Terrain Stride</span>
                <strong>${formatStride(state.snapshot?.renderOptions?.terrainSampleStride)}</strong>
              </div>
              <label class="setting-row speed-control" title="Scroll this control or use RMB + wheel in the viewport to adjust fly speed.">
                <span>Speed</span>
                <span class="inline-control">
                  <input id="camera-speed" type="number" min="0.5" max="1000" step="0.1" value="${formatSpeed(inputController.getMoveSpeedMultiplier())}" />
                  <strong>x</strong>
                </span>
              </label>
              <label class="setting-row camera-clip-control" title="Raise Near to reduce depth precision artifacts on huge worlds.">
                <span>Camera Near</span>
                <input id="camera-near" type="number" min="0.01" step="0.1" value="${formatClip(renderer.getCameraClip().near)}" />
              </label>
              <label class="setting-row camera-clip-control" title="Lower Far to clip distant terrain and reduce shadow-like depth artifacts.">
                <span>Camera Far</span>
                <input id="camera-far" type="number" min="1" step="100" value="${formatClip(renderer.getCameraClip().far)}" />
              </label>
              <label class="setting-row checkbox-control" title="Performance mode keeps the terrain wireframe-only and prefers lower idle rendering cost.">
                <span>Performance Mode</span>
                <input id="performance-mode" type="checkbox" ${state.performanceMode ? "checked" : ""} />
              </label>
              <label class="setting-row checkbox-control" title="Enable the transparent terrain fill. Wireframe-only is cheaper and is the default.">
                <span>Terrain Surface</span>
                <input id="terrain-surface" type="checkbox" ${state.showTerrainSurface ? "checked" : ""} />
              </label>
              <label class="setting-row camera-clip-control" title="Maximum render rate while moving the camera or receiving updates.">
                <span>Active FPS</span>
                <input id="active-fps" type="number" min="1" max="120" step="1" value="${state.activeFps}" />
              </label>
              <label class="setting-row camera-clip-control" title="Maximum render rate while the scene is idle.">
                <span>Idle FPS</span>
                <input id="idle-fps" type="number" min="1" max="60" step="1" value="${state.idleFps}" />
              </label>
              <div id="performance-hud" class="performance-hud"></div>
              </div>
              <div class="control-hints">
                <span>RMB look</span>
                <span>WASD + Q/E move</span>
                <span>RMB+Wheel speed</span>
                <span>MMB pan</span>
                <span>Alt+LMB orbit</span>
                <span>Wheel zoom</span>
              </div>
            </div>
          </div>
          <div class="layer-card ${state.layersCollapsed ? "is-collapsed" : ""}">
            <div class="layer-card-header">
              <div>
                <span class="eyebrow">Layers</span>
                <span class="hint">${getLayerSummary()}</span>
              </div>
              <button id="layer-card-toggle" class="ghost-button" aria-expanded="${state.layersCollapsed ? "false" : "true"}" title="${state.layersCollapsed ? "Expand layers" : "Collapse layers"}">
                ${state.layersCollapsed ? "Show" : "Hide"}
              </button>
            </div>
            <div id="layer-list" class="layer-list"></div>
          </div>
          <div id="viewport" class="viewport"></div>
          <button
            id="world-fullscreen-toggle"
            class="icon-button world-fullscreen-button"
            title="${state.worldFullscreen ? "Exit fullscreen" : "Fullscreen"}"
            aria-label="${state.worldFullscreen ? "Exit fullscreen" : "Fullscreen"}"
            aria-pressed="${state.worldFullscreen ? "true" : "false"}"
          >
            <span class="codicon codicon-${state.worldFullscreen ? "screen-normal" : "screen-full"}" aria-hidden="true"></span>
          </button>
        </section>
        <aside class="sidebar-panel inspector-panel">
          <section class="sidebar-section">
            <div class="section-title">
              <h2>Inspector</h2>
              <span class="hint">Selection details</span>
            </div>
            <div class="detail-grid">
              ${renderDetail("Dashboard", state.bootstrap?.baseUrl || "-")}
              ${renderDetail("World", getSelectedWorldSummary()?.name || "-")}
              ${renderDetail("Generated", state.snapshot?.generatedAtUtc || "-")}
            </div>
            <div class="inspector-divider"></div>
            <div id="inspector" class="detail-grid"></div>
            <div class="hint">Click an object row or scene object to inspect it. Double-click to focus the camera around its full bounds.</div>
          </section>
        </aside>
        </section>
        <section class="scene-explorer ${state.explorerCollapsed ? "is-collapsed" : ""} ${state.explorerFullscreen ? "is-fullscreen" : ""}">
          <div class="explorer-header">
            <div>
              <div class="eyebrow">Scene Explorer</div>
              <strong>${getUniqueSnapshotObjects().length} objects · ${getGizmos().length} gizmos · ${(state.snapshot?.partitions || []).length} partitions</strong>
            </div>
            <div class="explorer-actions">
              ${["objects", "gizmos", "partitions", "layers"].map((tab) => `
                <button class="tab-button ${state.explorerTab === tab ? "is-active" : ""}" data-explorer-tab="${tab}">
                  ${escapeHtml(capitalize(tab))}
                </button>
              `).join("")}
              <button id="explorer-fullscreen-toggle" class="icon-button" title="${state.explorerFullscreen ? "Exit explorer fullscreen" : "Explorer fullscreen"}" aria-label="${state.explorerFullscreen ? "Exit explorer fullscreen" : "Explorer fullscreen"}" aria-pressed="${state.explorerFullscreen ? "true" : "false"}">
                <span class="codicon codicon-${state.explorerFullscreen ? "screen-normal" : "screen-full"}" aria-hidden="true"></span>
              </button>
              <button id="explorer-toggle" class="ghost-button">${state.explorerCollapsed ? "Expand" : "Collapse"}</button>
            </div>
          </div>
          <div class="explorer-body">
            <section class="explorer-panel ${state.explorerTab === "objects" ? "is-active" : ""}">
              <div class="explorer-tools">
                <input id="object-search" class="search-input" placeholder="Filter by name, archetype, instance id, client id or zone..." value="${escapeAttribute(state.filter)}" />
                <span id="object-visible-hint" class="hint">${getVisibleSnapshotObjects().length} visible</span>
              </div>
              <div id="object-list" class="object-list object-table"></div>
            </section>
            <section class="explorer-panel ${state.explorerTab === "gizmos" ? "is-active" : ""}">
              <div id="gizmo-list" class="object-list object-table"></div>
            </section>
            <section class="explorer-panel ${state.explorerTab === "partitions" ? "is-active" : ""}">
              <div class="object-list object-table">${renderPartitionSummary()}</div>
            </section>
            <section class="explorer-panel ${state.explorerTab === "layers" ? "is-active" : ""}">
              <div class="layer-grid">${renderLayerCards()}</div>
            </section>
          </div>
        </section>
      </section>
    </div>
  `;

  worldSelect = document.getElementById("world-select");
  objectSearch = document.getElementById("object-search");
  objectList = document.getElementById("object-list");
  gizmoList = document.getElementById("gizmo-list");
  layerList = document.getElementById("layer-list");
  inspector = document.getElementById("inspector");
  viewport = document.getElementById("viewport");
  cameraSpeedInput = document.getElementById("camera-speed");
  cameraNearInput = document.getElementById("camera-near");
  cameraFarInput = document.getElementById("camera-far");
  activeFpsInput = document.getElementById("active-fps");
  idleFpsInput = document.getElementById("idle-fps");
  performanceModeInput = document.getElementById("performance-mode");
  terrainSurfaceInput = document.getElementById("terrain-surface");

  document.getElementById("world-fullscreen-toggle")?.addEventListener("click", () => {
    toggleWorldFullscreen();
  });

  document.getElementById("refresh-world")?.addEventListener("click", () => {
    loadWorldSnapshotStream();
  });

  document.getElementById("scene-controls-toggle")?.addEventListener("click", () => {
    state.sceneControlsCollapsed = !state.sceneControlsCollapsed;
    applySceneControlsState();
  });

  document.getElementById("explorer-toggle")?.addEventListener("click", () => {
    state.explorerCollapsed = !state.explorerCollapsed;
    applyExplorerCollapsedState();
  });

  document.getElementById("explorer-fullscreen-toggle")?.addEventListener("click", () => {
    toggleExplorerFullscreen();
  });

  document.getElementById("layer-card-toggle")?.addEventListener("click", () => {
    state.layersCollapsed = !state.layersCollapsed;
    applyLayerCardState();
  });

  for (const button of root.querySelectorAll("[data-explorer-tab]")) {
    button.addEventListener("click", () => {
      state.explorerTab = button.dataset.explorerTab || "objects";
      applyExplorerTabState();
    });
  }

  bindLayerInputs(root);
  bindPartitionSummary();

  worldSelect?.addEventListener("change", (event) => {
    const nextWorldIndex = Number(event.target.value);
    state.bootstrap = {
      ...(state.bootstrap || {}),
      selectedWorldIndex: nextWorldIndex,
    };
    const selected = (state.bootstrap?.worlds || []).find((world) => world.index === nextWorldIndex);
    state.snapshot = createEmptyWorldSnapshot(selected || { index: nextWorldIndex, name: `World ${nextWorldIndex}` });
    state.selectedObjectId = null;
    state.selectedPartitionKey = null;
    state.visiblePartitionKeys.clear();
    state.collapsedPartitionKeys.clear();
    renderShell();
    refreshWorld();
    loadWorldSnapshotStream();
  });

  objectSearch?.addEventListener("input", (event) => {
    state.filter = event.target.value || "";
    renderObjectList();
  });

  cameraSpeedInput?.addEventListener("change", (event) => {
    inputController.setMoveSpeedMultiplier(Number(event.target.value));
    updateSpeedUi();
  });

  cameraSpeedInput?.addEventListener("keydown", (event) => {
    event.stopPropagation();
  });

  cameraSpeedInput?.addEventListener("pointerdown", (event) => {
    event.stopPropagation();
  });

  cameraSpeedInput?.closest(".speed-control")?.addEventListener(
    "wheel",
    (event) => {
      event.preventDefault();
      event.stopPropagation();
      inputController.adjustMoveSpeedFromWheel(event.deltaY);
      updateSpeedUi();
    },
    { passive: false }
  );

  for (const input of [cameraNearInput, cameraFarInput]) {
    input?.addEventListener("change", () => {
      renderer.setCameraClip(Number(cameraNearInput?.value), Number(cameraFarInput?.value));
      updateClipUi();
    });

    input?.addEventListener("keydown", (event) => {
      event.stopPropagation();
    });

    input?.addEventListener("pointerdown", (event) => {
      event.stopPropagation();
    });
  }

  for (const input of [activeFpsInput, idleFpsInput]) {
    input?.addEventListener("change", () => {
      state.activeFps = clampNumber(Number(activeFpsInput?.value), 1, 120, 45);
      state.idleFps = clampNumber(Number(idleFpsInput?.value), 1, 60, 5);
      if (activeFpsInput) activeFpsInput.value = String(state.activeFps);
      if (idleFpsInput) idleFpsInput.value = String(state.idleFps);
      updatePerformanceOptions();
    });

    input?.addEventListener("keydown", (event) => {
      event.stopPropagation();
    });

    input?.addEventListener("pointerdown", (event) => {
      event.stopPropagation();
    });
  }

  performanceModeInput?.addEventListener("change", () => {
    state.performanceMode = Boolean(performanceModeInput.checked);
    if (state.performanceMode) {
      state.showTerrainSurface = false;
      state.idleFps = Math.min(state.idleFps, 5);
      state.activeFps = Math.min(state.activeFps, 45);
      renderShell();
      refreshWorld();
      return;
    }
    updatePerformanceOptions();
  });

  terrainSurfaceInput?.addEventListener("change", () => {
    state.showTerrainSurface = Boolean(terrainSurfaceInput.checked);
    if (state.showTerrainSurface) {
      state.performanceMode = false;
    }
    renderShell();
    refreshWorld();
  });

  if (!state.bootstrap || resolveEnvironmentMode() !== "3D") {
    renderer.dispose();
    viewport.innerHTML = `
      <div class="unsupported-state">
        <div>
          <h2>3D view unavailable</h2>
          <p>This extension currently renders only 3D Altruist worlds. The connected environment reported <strong>${escapeHtml(environmentMode)}</strong>.</p>
        </div>
      </div>
    `;
    renderInspector();
    renderObjectList();
    renderLayerList();
    disconnectWebSocket();
    return;
  }

  renderInspector();
  renderObjectList();
  renderGizmoList();
  renderLayerList();
  renderPerformanceHud();
  bindPartitionSummary();
  rerenderObjectCountChips();
  applyWorldFullscreenState();
}

async function toggleWorldFullscreen() {
  if (state.worldFullscreen) {
    await exitWorldFullscreen();
    return;
  }

  await enterWorldFullscreen();
}

async function enterWorldFullscreen() {
  state.worldFullscreen = true;
  applyWorldFullscreenState();

  const target = root.querySelector(".world-shell") || root;
  if (target?.requestFullscreen && !document.fullscreenElement) {
    try {
      await target.requestFullscreen();
    } catch {
      // VS Code webviews may deny native fullscreen. The CSS fullscreen fallback still works.
    }
  }

  scheduleRendererResize();
}

async function exitWorldFullscreen() {
  state.worldFullscreen = false;
  applyWorldFullscreenState();

  if (document.fullscreenElement && document.exitFullscreen) {
    try {
      await document.exitFullscreen();
    } catch {
      // Keep the CSS fallback state authoritative even if native fullscreen exit fails.
    }
  }

  scheduleRendererResize();
}

function applyWorldFullscreenState() {
  const shell = root.querySelector(".world-shell");
  shell?.classList.toggle("is-fullscreen", state.worldFullscreen);

  const button = document.getElementById("world-fullscreen-toggle");
  if (button) {
    const label = state.worldFullscreen ? "Exit fullscreen" : "Fullscreen";
    button.title = label;
    button.setAttribute("aria-label", label);
    button.setAttribute("aria-pressed", state.worldFullscreen ? "true" : "false");
    button.innerHTML = `<span class="codicon codicon-${state.worldFullscreen ? "screen-normal" : "screen-full"}" aria-hidden="true"></span>`;
  }

  scheduleRendererResize();
}

function scheduleRendererResize() {
  requestAnimationFrame(() => {
    renderer.resize();
    requestAnimationFrame(() => renderer.resize());
  });
}

function loadWorldSnapshotStream() {
  if (!state.bootstrap || resolveEnvironmentMode() !== "3D") {
    return;
  }

  const worldIndex = state.bootstrap.selectedWorldIndex;
  if (worldIndex == null) {
    return;
  }

  const requestId = state.streamRequestId + 1;
  state.streamRequestId = requestId;
  state.streamState = "loading";
  state.streamError = "";
  state.streamLoadedPartitions = 0;

  const selected = (state.bootstrap?.worlds || []).find((world) => world.index === worldIndex);
  state.snapshot = createEmptyWorldSnapshot(selected || { index: worldIndex, name: `World ${worldIndex}` });
  reconcileSceneState();
  refreshWorld();

  vscode.postMessage({
    type: "world:load-stream",
    worldIndex,
    requestId,
  });
}

function appendStreamLine(line) {
  const trimmed = String(line || "").trim();
  if (!trimmed || !state.snapshot) {
    return;
  }

  const partition = JSON.parse(trimmed);
  appendStreamPartition(partition);
}

function appendStreamPartition(partition) {
  if (!partition || !state.snapshot) {
    return;
  }

  state.snapshot.partitions.push(partition);
  state.streamLoadedPartitions += 1;

  const key = getPartitionKey(partition);
  if (!state.selectedPartitionKey) {
    state.selectedPartitionKey = key;
  }
  if (!state.visiblePartitionKeys.size) {
    state.visiblePartitionKeys.add(key);
  }

  renderer.appendPartition(partition);
  scheduleStreamRender();
}

function isCurrentStreamMessage(message) {
  return (
    message?.worldIndex === state.bootstrap?.selectedWorldIndex &&
    Number(message?.requestId) === state.streamRequestId
  );
}

function scheduleStreamRender() {
  if (state.streamRenderQueued) {
    return;
  }

  state.streamRenderQueued = true;
  requestAnimationFrame(() => {
    state.streamRenderQueued = false;
    reconcileSceneState();
    renderer.setSelectedObject(state.selectedObjectId);
    renderInspector();
    renderObjectList();
    renderGizmoList();
    renderLayerList();
    bindPartitionSummary();
    rerenderObjectCountChips();
  });
}

function finishStreamRender() {
  reconcileSceneState();
  renderer.setSelectedObject(state.selectedObjectId);
  renderInspector();
  renderObjectList();
  renderGizmoList();
  renderLayerList();
  bindPartitionSummary();
  rerenderObjectCountChips();
  rerenderToolbarOnly();
}

function createEmptyWorldSnapshot(world) {
  return {
    worldIndex: world?.index ?? 0,
    worldName: world?.name || `World ${world?.index ?? 0}`,
    generatedAtUtc: new Date().toISOString(),
    renderOptions: {
      renderScale: 1,
      terrainSampleStride: 1,
    },
    partitions: [],
    gizmos: [],
  };
}

function refreshWorld() {
  if (!state.bootstrap || !viewport || resolveEnvironmentMode() !== "3D") {
    renderInspector();
    renderObjectList();
    renderLayerList();
    return;
  }

  if (state.error) {
    renderer.dispose();
    viewport.innerHTML = `
      <div class="error-state">
        <div>
          <h2>World load failed</h2>
          <p>${escapeHtml(state.error)}</p>
        </div>
      </div>
    `;
    renderInspector();
    renderObjectList();
    renderLayerList();
    return;
  }

  if (!state.snapshot) {
    renderer.dispose();
    viewport.innerHTML = `<div class="loading">Waiting for world snapshot...</div>`;
    renderInspector();
    renderObjectList();
    renderLayerList();
    return;
  }

  if (!renderer.renderer || viewport.children.length === 0 || viewport.querySelector("canvas") === null) {
    renderer.mount(viewport);
    viewport.addEventListener("click", handleViewportClick);
    viewport.addEventListener("dblclick", handleViewportDoubleClick);
  }

  updatePerformanceOptions();
  renderer.setSnapshot(state.snapshot);
  renderer.setSceneVisibility(state.visiblePartitionKeys, state.hiddenObjectIds);
  renderer.setGizmoVisibility(state.hiddenGizmoIds, state.hiddenGizmoCategories);

  reconcileSceneState();
  renderer.setSceneVisibility(state.visiblePartitionKeys, state.hiddenObjectIds);

  renderer.setSelectedObject(state.selectedObjectId);
  if (state.selectedObjectId) {
    renderer.focusOnObject(state.selectedObjectId);
  }

  renderInspector();
  renderObjectList();
  renderGizmoList();
  renderLayerList();
  bindPartitionSummary();
  rerenderObjectCountChips();
}

function renderObjectList() {
  if (!objectList) {
    return;
  }

  const query = String(state.filter || "").trim().toLowerCase();
  const partitions = getPartitions();

  if (!partitions.length) {
    objectList.innerHTML = `<div class="empty-state">No partitions in this world snapshot.</div>`;
    return;
  }

  objectList.innerHTML = partitions
    .map((partition) => {
      const key = getPartitionKey(partition);
      const collapsed = state.collapsedPartitionKeys.has(key);
      const visible = state.visiblePartitionKeys.has(key);
      const objects = (partition.objects || []).filter((object) => objectMatchesQuery(object, query));

      return `
        <div class="partition-block ${key === state.selectedPartitionKey ? "is-selected" : ""}">
          <div class="partition-row ${key === state.selectedPartitionKey ? "is-selected" : ""}" role="button" tabindex="0" data-partition-key="${escapeAttribute(key)}">
            <button class="visibility-button ${visible ? "is-visible" : ""}" title="${visible ? "Hide partition" : "Show partition"}" data-partition-eye="${escapeAttribute(key)}">${renderVisibilityIcon(visible)}</button>
            <span class="chevron ${collapsed ? "is-collapsed" : ""}" data-partition-collapse="${escapeAttribute(key)}">&#9654;</span>
            <span class="partition-title">Partition [${partitionIndex(partition, "x")}, ${partitionIndex(partition, "y")}, ${partitionIndex(partition, "z")}]</span>
            <span class="partition-meta">${objects.length}/${(partition.objects || []).length} objects</span>
          </div>
          ${collapsed ? "" : `
            <div class="partition-objects">
              ${objects.length ? objects.map((object) => {
                const objectVisible = visible && !state.hiddenObjectIds.has(object.instanceId);
                return `
                  <div class="object-row ${object.instanceId === state.selectedObjectId ? "is-selected" : ""}" role="button" tabindex="0" data-object-id="${escapeAttribute(object.instanceId)}">
                    <button class="visibility-button ${objectVisible ? "is-visible" : ""}" title="${objectVisible ? "Hide object" : "Show object"}" data-object-eye="${escapeAttribute(object.instanceId)}">${renderVisibilityIcon(objectVisible)}</button>
                    <div class="object-row-body">
                      <div class="object-name">
                        <strong>${escapeHtml(formatObjectName(object))}</strong>
                        ${object.name && object.archetype ? `<span class="chip">${escapeHtml(object.archetype)}</span>` : ""}
                        ${object.clientId ? `<span class="chip">${escapeHtml(object.clientId)}</span>` : ""}
                      </div>
                      <div class="object-meta">${escapeHtml(object.instanceId)}</div>
                      <div class="object-meta">Position: ${formatVector(object.transform?.position)}</div>
                    </div>
                  </div>
                `;
              }).join("") : `<div class="empty-state compact">No objects matched this partition/filter.</div>`}
            </div>
          `}
        </div>
      `;
    })
    .join("");

  for (const button of objectList.querySelectorAll("[data-partition-key]")) {
    button.addEventListener("click", () => {
      selectPartition(button.dataset.partitionKey);
    });
  }

  for (const button of objectList.querySelectorAll("[data-partition-eye]")) {
    button.addEventListener("click", (event) => {
      event.stopPropagation();
      togglePartitionVisibility(button.dataset.partitionEye);
    });
  }

  for (const button of objectList.querySelectorAll("[data-partition-collapse]")) {
    button.addEventListener("click", (event) => {
      event.stopPropagation();
      togglePartitionCollapsed(button.dataset.partitionCollapse);
    });
  }

  for (const button of objectList.querySelectorAll("[data-object-id]")) {
    button.addEventListener("click", () => {
      selectObject(button.dataset.objectId, false);
    });
    button.addEventListener("dblclick", () => {
      selectObject(button.dataset.objectId, true);
    });
  }

  for (const button of objectList.querySelectorAll("[data-object-eye]")) {
    button.addEventListener("click", (event) => {
      event.stopPropagation();
      toggleObjectVisibility(button.dataset.objectEye);
    });
  }
}

function bindPartitionSummary() {
  for (const button of root.querySelectorAll(".scene-explorer [data-partition-key]")) {
    button.addEventListener("click", () => {
      selectPartition(button.dataset.partitionKey);
    });
  }

  for (const button of root.querySelectorAll(".scene-explorer [data-partition-eye]")) {
    button.addEventListener("click", (event) => {
      event.stopPropagation();
      togglePartitionVisibility(button.dataset.partitionEye);
    });
  }
}

function renderGizmoList() {
  if (!gizmoList) {
    return;
  }

  const gizmos = getGizmos()
    .slice()
    .sort((left, right) => {
      const leftKey = `${left.category}|${left.id}`;
      const rightKey = `${right.category}|${right.id}`;
      return leftKey.localeCompare(rightKey);
    });

  if (!gizmos.length) {
    gizmoList.innerHTML = `<div class="empty-state">No dashboard gizmos registered.</div>`;
    return;
  }

  const categories = Array.from(new Set(gizmos.map((gizmo) => gizmo.category || "uncategorized")))
    .sort((left, right) => left.localeCompare(right));

  gizmoList.innerHTML = `
    <div class="object-row">
      <div class="object-name"><strong>Categories</strong></div>
      ${categories
        .map((category) => `
          <label class="object-meta">
            <input type="checkbox" data-gizmo-category="${escapeAttribute(category)}" ${state.hiddenGizmoCategories.has(category.toLowerCase()) ? "" : "checked"} />
            ${escapeHtml(category)}
          </label>
        `)
        .join("")}
    </div>
    ${gizmos
      .map((gizmo) => {
        const hidden = state.hiddenGizmoIds.has(gizmo.id);
        return `
          <label class="object-row">
            <div class="object-name">
              <input type="checkbox" data-gizmo-id="${escapeAttribute(gizmo.id)}" ${hidden ? "" : "checked"} />
              <strong>${escapeHtml(gizmo.label || gizmo.id)}</strong>
              <span class="chip">${escapeHtml(gizmo.category || "-")}</span>
            </div>
            <div class="object-meta">${escapeHtml(gizmo.id)}</div>
            <div class="object-meta">${escapeHtml(gizmo.type || "shape")} &middot; ${escapeHtml(gizmo.source || "-")}</div>
          </label>
        `;
      })
      .join("")}
  `;

  for (const input of gizmoList.querySelectorAll("[data-gizmo-category]")) {
    input.addEventListener("change", () => {
      const category = String(input.dataset.gizmoCategory || "").toLowerCase();
      if (input.checked) {
        state.hiddenGizmoCategories.delete(category);
      } else {
        state.hiddenGizmoCategories.add(category);
      }
      renderer.setGizmoVisibility(state.hiddenGizmoIds, state.hiddenGizmoCategories);
      renderGizmoList();
    });
  }

  for (const input of gizmoList.querySelectorAll("[data-gizmo-id]")) {
    input.addEventListener("change", () => {
      const id = input.dataset.gizmoId || "";
      if (input.checked) {
        state.hiddenGizmoIds.delete(id);
      } else {
        state.hiddenGizmoIds.add(id);
      }
      renderer.setGizmoVisibility(state.hiddenGizmoIds, state.hiddenGizmoCategories);
      renderGizmoList();
    });
  }
}

function renderLayerList() {
  if (!layerList) {
    return;
  }

  layerList.innerHTML = renderLayerCards();
  bindLayerInputs(layerList);
}

function renderLayerCards() {
  const categories = getGizmoCategories();
  const objectCount = getUniqueSnapshotObjects().length;
  const visibleObjectCount = getVisibleSnapshotObjects().length;

  return `
    <label class="layer-row">
      <span>
        <strong>Objects</strong>
        <small>${visibleObjectCount}/${objectCount} visible</small>
      </span>
      <input type="checkbox" checked disabled />
    </label>
    ${categories.map((category) => {
      const hidden = state.hiddenGizmoCategories.has(category.toLowerCase());
      const count = getGizmos().filter((gizmo) => (gizmo.category || "uncategorized") === category).length;
      return `
        <label class="layer-row">
          <span>
            <strong>${escapeHtml(category)}</strong>
            <small>${count} gizmos</small>
          </span>
          <input type="checkbox" data-layer-category="${escapeAttribute(category)}" ${hidden ? "" : "checked"} />
        </label>
      `;
    }).join("")}
  `;
}

function bindLayerInputs(scope) {
  for (const input of scope.querySelectorAll("[data-layer-category]")) {
    input.addEventListener("change", () => {
      const category = String(input.dataset.layerCategory || "").toLowerCase();
      if (input.checked) {
        state.hiddenGizmoCategories.delete(category);
      } else {
        state.hiddenGizmoCategories.add(category);
      }
      renderer.setGizmoVisibility(state.hiddenGizmoIds, state.hiddenGizmoCategories);
      renderGizmoList();
      renderLayerList();
    });
  }
}

function renderInspector() {
  if (!inspector) {
    return;
  }

  const object = findObject(state.selectedObjectId);
  if (!object) {
    inspector.innerHTML = `<div class="empty-state">Select an object from the list or click it in the scene.</div>`;
    return;
  }

  inspector.innerHTML = `
    ${renderDetail("Name", object.name || "-")}
    ${renderDetail("Archetype", object.archetype || formatObjectName(object))}
    ${renderDetail("Instance", object.instanceId)}
    ${renderDetail("Client", object.clientId || "-")}
    ${renderDetail("Zone", object.zoneId || "-")}
    ${renderDetail("Position", formatVector(object.transform?.position))}
    ${renderDetail("Colliders", String((object.colliders || []).length))}
  `;
}

function selectObject(instanceId, focus) {
  state.selectedObjectId = instanceId || null;
  renderer.setSelectedObject(state.selectedObjectId);
  if (focus && state.selectedObjectId) {
    renderer.focusOnObject(state.selectedObjectId);
  }
  renderInspector();
  renderObjectList();
}

function selectPartition(partitionKey) {
  if (!partitionKey) {
    return;
  }

  state.selectedPartitionKey = partitionKey;
  state.visiblePartitionKeys = new Set([partitionKey]);
  state.collapsedPartitionKeys.delete(partitionKey);

  const partition = getPartitions().find((item) => getPartitionKey(item) === partitionKey);
  const firstObject = (partition?.objects || []).find((object) => !state.hiddenObjectIds.has(object.instanceId));
  state.selectedObjectId = firstObject?.instanceId || null;

  renderer.setSceneVisibility(state.visiblePartitionKeys, state.hiddenObjectIds);
  renderer.setSelectedObject(state.selectedObjectId);
  if (state.selectedObjectId) {
    renderer.focusOnObject(state.selectedObjectId);
  }

  renderInspector();
  renderObjectList();
  rerenderObjectCountChips();
}

function togglePartitionVisibility(partitionKey) {
  if (!partitionKey) {
    return;
  }

  if (state.visiblePartitionKeys.has(partitionKey)) {
    state.visiblePartitionKeys.delete(partitionKey);
  } else {
    state.visiblePartitionKeys.add(partitionKey);
  }

  if (!state.visiblePartitionKeys.size && state.selectedPartitionKey) {
    state.visiblePartitionKeys.add(state.selectedPartitionKey);
  }

  renderer.setSceneVisibility(state.visiblePartitionKeys, state.hiddenObjectIds);
  if (!findObject(state.selectedObjectId)) {
    state.selectedObjectId = getVisibleObjects()[0]?.instanceId || null;
    renderer.setSelectedObject(state.selectedObjectId);
  }
  renderInspector();
  renderObjectList();
  rerenderObjectCountChips();
}

function togglePartitionCollapsed(partitionKey) {
  if (!partitionKey) {
    return;
  }

  if (state.collapsedPartitionKeys.has(partitionKey)) {
    state.collapsedPartitionKeys.delete(partitionKey);
  } else {
    state.collapsedPartitionKeys.add(partitionKey);
  }

  renderObjectList();
}

function toggleObjectVisibility(instanceId) {
  if (!instanceId) {
    return;
  }

  if (state.hiddenObjectIds.has(instanceId)) {
    state.hiddenObjectIds.delete(instanceId);
  } else {
    state.hiddenObjectIds.add(instanceId);
  }

  renderer.setSceneVisibility(state.visiblePartitionKeys, state.hiddenObjectIds);
  if (!findObject(state.selectedObjectId)) {
    state.selectedObjectId = getVisibleObjects()[0]?.instanceId || null;
    renderer.setSelectedObject(state.selectedObjectId);
  }
  renderInspector();
  renderObjectList();
  rerenderObjectCountChips();
}

function handleViewportClick(event) {
  const instanceId = renderer.pick(event.clientX, event.clientY);
  if (!instanceId) {
    return;
  }

  selectObject(instanceId, false);
}

function handleViewportDoubleClick(event) {
  const instanceId = renderer.pick(event.clientX, event.clientY);
  if (!instanceId) {
    return;
  }

  selectObject(instanceId, true);
}

function connectWebSocket() {
  disconnectWebSocket();

  const websocketUrl = state.bootstrap?.websocketUrl;
  if (!websocketUrl || resolveEnvironmentMode() !== "3D") {
    state.shouldReconnect = false;
    state.websocketState = "idle";
    state.websocketError = websocketUrl ? "" : "World websocket URL was not provided.";
    rerenderToolbarOnly();
    return;
  }

  try {
    state.shouldReconnect = true;
    ws = new WebSocket(websocketUrl);
    state.websocketState = "connecting";
    state.websocketError = "";
    rerenderToolbarOnly();

    ws.addEventListener("open", () => {
      state.websocketState = "open";
      state.websocketError = "";
      rerenderToolbarOnly();
      if (state.streamState !== "loading") {
        loadWorldSnapshotStream();
      }
    });

    ws.addEventListener("message", async (event) => {
      const raw = typeof event.data === "string" ? event.data : await event.data.text();
      handleRealtimeMessage(raw);
    });

    ws.addEventListener("error", () => {
      state.websocketState = "error";
      state.websocketError = "The dashboard websocket is unreachable.";
      rerenderToolbarOnly();
    });

    ws.addEventListener("close", () => {
      ws = null;
      if (state.shouldReconnect && resolveEnvironmentMode() === "3D") {
        state.websocketState = "closed";
        state.websocketError = "World stream closed. Retrying in 2s.";
        rerenderToolbarOnly();

        clearTimeout(state.reconnectTimer);
        state.reconnectTimer = window.setTimeout(() => {
          connectWebSocket();
        }, 2000);
      }
    });
  } catch (error) {
    state.websocketState = "error";
    state.websocketError = error instanceof Error ? error.message : String(error);
    rerenderToolbarOnly();
  }
}

function disconnectWebSocket() {
  state.shouldReconnect = false;
  clearTimeout(state.reconnectTimer);
  state.reconnectTimer = null;

  if (ws) {
    try {
      ws.close();
    } catch {
      // ignore
    }
    ws = null;
  }
}

function handleRealtimeMessage(raw) {
  let envelope;
  try {
    envelope = JSON.parse(raw);
  } catch {
    return;
  }

  const packet = unwrapWorldStatePacket(envelope);
  if (!packet) {
    return;
  }

  const selectedWorldIndex = state.bootstrap?.selectedWorldIndex;
  if (selectedWorldIndex == null || packet.worldIndex !== selectedWorldIndex) {
    return;
  }

  removeSnapshotObjects(packet.removedObjectIds);
  applySnapshotObjectUpdates(packet.partitions);
  applySnapshotGizmoUpdates(packet);
  reconcileSceneState();
  renderer.applyRealtimePacket(packet);
  if (!findObject(state.selectedObjectId)) {
    state.selectedObjectId = getVisibleSnapshotObjects()[0]?.instanceId || null;
  }
  renderer.setSelectedObject(state.selectedObjectId);
  renderer.setGizmoVisibility(state.hiddenGizmoIds, state.hiddenGizmoCategories);
  renderInspector();
  renderObjectList();
  renderGizmoList();
  renderLayerList();
  rerenderObjectCountChips();
}

function unwrapWorldStatePacket(envelope) {
  if (!envelope || typeof envelope !== "object") {
    return null;
  }

  if (envelope.type === "DashboardWorldObjectStatePacket" && envelope.message) {
    return envelope.message;
  }

  if (Number(envelope.messageCode) === DASHBOARD_WORLD_OBJECT_STATE_CODE && envelope.message) {
    return envelope.message;
  }

  if (Number(envelope.message?.messageCode) === DASHBOARD_WORLD_OBJECT_STATE_CODE) {
    return envelope.message;
  }

  if (Number(envelope.messageCode) === DASHBOARD_WORLD_OBJECT_STATE_CODE) {
    return envelope;
  }

  return null;
}

function applySnapshotObjectUpdates(partitions) {
  if (!state.snapshot || !Array.isArray(partitions)) {
    return;
  }

  for (const partition of partitions) {
    for (const update of partition?.objects || []) {
      const id = update?.instanceId || update?.id;
      if (!id) {
        continue;
      }

      const { partition: snapshotPartition, created } = ensureSnapshotPartition(partition);
      const object = findSnapshotObject(id) || createSnapshotObjectFromRealtime(update, id);
      if (created) {
        state.visiblePartitionKeys.add(getPartitionKey(snapshotPartition));
      }
      removeSnapshotObjectReferences(id, snapshotPartition);
      if (!snapshotPartition.objects.some((item) => item?.instanceId === id)) {
        snapshotPartition.objects.push(object);
      }

      const position = normalizeRealtimeVector(update.position);
      if (position) {
        object.transform = object.transform || {};
        object.transform.position = position;
      }
      if (typeof update.name === "string") {
        object.name = update.name;
      }
      if (typeof update.archetype === "string") {
        object.archetype = update.archetype;
      }
    }
  }

  pruneEmptySnapshotPartitions();
}

function removeSnapshotObjectReferences(instanceId, exceptPartition = null) {
  for (const partition of state.snapshot?.partitions || []) {
    if (partition === exceptPartition) {
      continue;
    }

    partition.objects = (partition.objects || []).filter((object) => object?.instanceId !== instanceId);
  }
}

function pruneEmptySnapshotPartitions() {
  if (!state.snapshot) {
    return;
  }

  state.snapshot.partitions = (state.snapshot.partitions || [])
    .filter((partition) => (partition.objects || []).length > 0);
}

function ensureSnapshotPartition(partition) {
  const key = getPartitionKey(partition);
  let snapshotPartition = getPartitions().find((item) => getPartitionKey(item) === key);
  let created = false;

  if (!snapshotPartition) {
    snapshotPartition = {
      x: partitionIndex(partition, "x"),
      y: partitionIndex(partition, "y"),
      z: partitionIndex(partition, "z"),
      objects: [],
    };
    state.snapshot.partitions.push(snapshotPartition);
    created = true;
  }

  if (!Array.isArray(snapshotPartition.objects)) {
    snapshotPartition.objects = [];
  }

  return { partition: snapshotPartition, created };
}

function createSnapshotObjectFromRealtime(update, instanceId) {
  return {
    instanceId,
    archetype: update?.archetype || "",
    name: update?.name || "",
    zoneId: "",
    clientId: "",
    expired: false,
    transform: {
      position: normalizeRealtimeVector(update?.position) || { x: 0, y: 0, z: 0 },
    },
    colliders: [],
  };
}

function normalizeRealtimeVector(vector) {
  if (!vector || typeof vector !== "object") {
    return null;
  }

  return {
    x: Number(vector.x ?? vector.X ?? 0),
    y: Number(vector.y ?? vector.Y ?? 0),
    z: Number(vector.z ?? vector.Z ?? 0),
  };
}

function removeSnapshotObjects(instanceIds) {
  if (!state.snapshot || !Array.isArray(instanceIds) || instanceIds.length === 0) {
    return;
  }

  const removed = new Set(instanceIds.filter(Boolean));
  if (!removed.size) {
    return;
  }

  for (const partition of state.snapshot.partitions || []) {
    partition.objects = (partition.objects || []).filter((object) => !removed.has(object?.instanceId));
  }

  state.snapshot.partitions = (state.snapshot.partitions || []).filter((partition) => (partition.objects || []).length > 0);

  for (const id of removed) {
    state.hiddenObjectIds.delete(id);
  }
}

function applySnapshotGizmoUpdates(packet) {
  if (!state.snapshot) {
    return;
  }

  const removed = new Set((packet?.removedGizmoIds || []).filter(Boolean));
  const byId = new Map();

  for (const gizmo of state.snapshot.gizmos || []) {
    if (gizmo?.id && !removed.has(gizmo.id)) {
      byId.set(gizmo.id, gizmo);
    }
  }

  for (const gizmo of packet?.gizmos || []) {
    if (gizmo?.id) {
      byId.set(gizmo.id, gizmo);
    }
  }

  state.snapshot.gizmos = Array.from(byId.values());
}

function getObjects() {
  return renderer.getObjects();
}

function getVisibleObjects() {
  return renderer.getObjects();
}

function getVisibleSnapshotObjects() {
  const byId = new Map();
  for (const partition of getPartitions()) {
    if (!state.visiblePartitionKeys.has(getPartitionKey(partition))) {
      continue;
    }

    for (const object of partition.objects || []) {
      if (object?.instanceId && !state.hiddenObjectIds.has(object.instanceId)) {
        byId.set(object.instanceId, object);
      }
    }
  }

  return Array.from(byId.values());
}

function getPartitions() {
  return state.snapshot?.partitions || [];
}

function getUniqueSnapshotObjects() {
  const byId = new Map();
  for (const partition of getPartitions()) {
    for (const object of partition.objects || []) {
      if (object?.instanceId && !byId.has(object.instanceId)) {
        byId.set(object.instanceId, object);
      }
    }
  }
  return Array.from(byId.values());
}

function getGizmos() {
  return state.snapshot?.gizmos || [];
}

function getGizmoCategories() {
  return Array.from(new Set(getGizmos().map((gizmo) => gizmo.category || "uncategorized")))
    .sort((left, right) => left.localeCompare(right));
}

function findObject(instanceId) {
  if (!instanceId) {
    return null;
  }
  return renderer.getObject(instanceId) || findSnapshotObject(instanceId);
}

function findSnapshotObject(instanceId) {
  for (const partition of getPartitions()) {
    for (const object of partition.objects || []) {
      if (object?.instanceId === instanceId) {
        return object;
      }
    }
  }
  return null;
}

function reconcileSceneState() {
  const partitions = getPartitions();
  const partitionKeys = new Set(partitions.map(getPartitionKey));

  for (const key of Array.from(state.collapsedPartitionKeys)) {
    if (!partitionKeys.has(key)) {
      state.collapsedPartitionKeys.delete(key);
    }
  }

  for (const key of Array.from(state.visiblePartitionKeys)) {
    if (!partitionKeys.has(key)) {
      state.visiblePartitionKeys.delete(key);
    }
  }

  if (!state.selectedPartitionKey || !partitionKeys.has(state.selectedPartitionKey)) {
    state.selectedPartitionKey = partitions[0] ? getPartitionKey(partitions[0]) : null;
  }

  if (!state.visiblePartitionKeys.size && state.selectedPartitionKey) {
    state.visiblePartitionKeys.add(state.selectedPartitionKey);
  }

  const visibleObjectIds = new Set();
  for (const partition of partitions) {
    const key = getPartitionKey(partition);
    if (!state.visiblePartitionKeys.has(key)) {
      continue;
    }

    for (const object of partition.objects || []) {
      if (object?.instanceId && !state.hiddenObjectIds.has(object.instanceId)) {
        visibleObjectIds.add(object.instanceId);
      }
    }
  }

  if (!state.selectedObjectId || !visibleObjectIds.has(state.selectedObjectId)) {
    state.selectedObjectId = Array.from(visibleObjectIds)[0] || null;
  }
}

function objectMatchesQuery(object, query) {
  if (!query) {
    return true;
  }

  return [object.name, object.archetype, object.instanceId, object.clientId, object.zoneId]
    .filter(Boolean)
    .some((value) => String(value).toLowerCase().includes(query));
}

function getPartitionKey(partition) {
  return `${partitionIndex(partition, "x")}:${partitionIndex(partition, "y")}:${partitionIndex(partition, "z")}`;
}

function partitionIndex(partition, axis) {
  const upper = `index${axis.toUpperCase()}`;
  return Number(partition?.[upper] ?? partition?.[axis] ?? 0);
}

function getSelectedWorldSummary() {
  const worlds = state.bootstrap?.worlds || [];
  return worlds.find((world) => world.index === state.bootstrap?.selectedWorldIndex) || null;
}

function rerenderToolbarOnly() {
  const chip = buildWebsocketChip();
  const connectionChip = root.querySelector('[data-role="socket-chip"]');
  if (!connectionChip) {
    renderShell();
    refreshWorld();
    return;
  }

  connectionChip.outerHTML = chip;
}

function rerenderObjectCountChips() {
  const count = getVisibleSnapshotObjects().length;
  const total = getUniqueSnapshotObjects().length;
  const chip = document.getElementById("object-count-chip");
  if (chip) {
    chip.textContent = `${count} visible / ${total} total`;
  }

  const hint = document.getElementById("object-visible-hint");
  if (hint) {
    hint.textContent = `${count} visible`;
  }
}

function applySceneControlsState() {
  const card = root.querySelector(".scene-info-card");
  const button = document.getElementById("scene-controls-toggle");
  if (!card || !button) {
    return;
  }

  card.classList.toggle("is-collapsed", state.sceneControlsCollapsed);
  card.dataset.collapsed = state.sceneControlsCollapsed ? "true" : "false";
  button.textContent = state.sceneControlsCollapsed ? "Show" : "Hide";
  button.setAttribute("aria-expanded", state.sceneControlsCollapsed ? "false" : "true");
  button.title = state.sceneControlsCollapsed ? "Expand scene controls" : "Collapse scene controls";
}

function applyExplorerCollapsedState() {
  const explorer = root.querySelector(".scene-explorer");
  const button = document.getElementById("explorer-toggle");
  if (!explorer || !button) {
    return;
  }

  explorer.classList.toggle("is-collapsed", state.explorerCollapsed);
  button.textContent = state.explorerCollapsed ? "Expand" : "Collapse";
}

function toggleExplorerFullscreen() {
  if (state.explorerFullscreen) {
    exitExplorerFullscreen();
    return;
  }

  enterExplorerFullscreen();
}

function enterExplorerFullscreen() {
  state.explorerFullscreen = true;
  state.explorerCollapsed = false;
  applyExplorerFullscreenState();
  applyExplorerCollapsedState();
}

function exitExplorerFullscreen() {
  state.explorerFullscreen = false;
  applyExplorerFullscreenState();
}

function applyExplorerFullscreenState() {
  const explorer = root.querySelector(".scene-explorer");
  explorer?.classList.toggle("is-fullscreen", state.explorerFullscreen);

  const button = document.getElementById("explorer-fullscreen-toggle");
  if (!button) {
    return;
  }

  const label = state.explorerFullscreen ? "Exit explorer fullscreen" : "Explorer fullscreen";
  button.title = label;
  button.setAttribute("aria-label", label);
  button.setAttribute("aria-pressed", state.explorerFullscreen ? "true" : "false");
  button.innerHTML = `<span class="codicon codicon-${state.explorerFullscreen ? "screen-normal" : "screen-full"}" aria-hidden="true"></span>`;
}

function applyLayerCardState() {
  const card = root.querySelector(".layer-card");
  const button = document.getElementById("layer-card-toggle");
  if (!card || !button) {
    return;
  }

  card.classList.toggle("is-collapsed", state.layersCollapsed);
  button.textContent = state.layersCollapsed ? "Show" : "Hide";
  button.setAttribute("aria-expanded", state.layersCollapsed ? "false" : "true");
  button.title = state.layersCollapsed ? "Expand layers" : "Collapse layers";
}

function applyExplorerTabState() {
  for (const button of root.querySelectorAll("[data-explorer-tab]")) {
    button.classList.toggle("is-active", button.dataset.explorerTab === state.explorerTab);
  }

  for (const panel of root.querySelectorAll(".explorer-panel")) {
    panel.classList.remove("is-active");
  }

  const tabs = ["objects", "gizmos", "partitions", "layers"];
  const index = Math.max(0, tabs.indexOf(state.explorerTab));
  root.querySelectorAll(".explorer-panel")[index]?.classList.add("is-active");

  if (state.explorerTab === "objects") {
    renderObjectList();
  } else if (state.explorerTab === "gizmos") {
    renderGizmoList();
  } else if (state.explorerTab === "layers") {
    renderLayerList();
  } else {
    bindPartitionSummary();
  }
}

function updatePerformanceOptions() {
  renderer.setPerformanceOptions({
    activeFps: state.activeFps,
    idleFps: state.idleFps,
    showTerrainSurface: state.showTerrainSurface,
  });
  renderPerformanceHud();
}

function renderPerformanceHud() {
  const target = document.getElementById("performance-hud");
  if (!target) {
    return;
  }

  const stats = state.perfStats || renderer.getPerformanceStats();
  target.innerHTML = `
    <div class="perf-row">
      <span>FPS</span>
      <strong>${formatPerfNumber(stats.fps, 1)}</strong>
    </div>
    <div class="perf-row">
      <span>Draw Calls</span>
      <strong>${formatPerfNumber(stats.drawCalls, 0)}</strong>
    </div>
    <div class="perf-row">
      <span>Triangles</span>
      <strong>${formatPerfNumber(stats.triangles, 0)}</strong>
    </div>
    <div class="perf-row">
      <span>Geometries</span>
      <strong>${formatPerfNumber(stats.geometries, 0)}</strong>
    </div>
  `;
}

function renderPartitionSummary() {
  const partitions = getPartitions();
  if (!partitions.length) {
    return `<div class="empty-state compact">No partitions in this world snapshot.</div>`;
  }

  return partitions.map((partition) => {
    const key = getPartitionKey(partition);
    const visible = state.visiblePartitionKeys.has(key);
    const count = (partition.objects || []).length;
    return `
      <div class="partition-row ${key === state.selectedPartitionKey ? "is-selected" : ""}" data-partition-key="${escapeAttribute(key)}">
        <button class="visibility-button ${visible ? "is-visible" : ""}" data-partition-eye="${escapeAttribute(key)}">${renderVisibilityIcon(visible)}</button>
        <span class="partition-title">Partition [${partitionIndex(partition, "x")}, ${partitionIndex(partition, "y")}, ${partitionIndex(partition, "z")}]</span>
        <span class="partition-meta">${count} objects</span>
      </div>
    `;
  }).join("");
}

function renderVisibilityIcon(visible) {
  if (visible) {
    return `
      <svg class="visibility-icon" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
        <path d="M2.4 12s3.6-6.2 9.6-6.2S21.6 12 21.6 12s-3.6 6.2-9.6 6.2S2.4 12 2.4 12Z" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
        <circle cx="12" cy="12" r="3.1" fill="none" stroke="currentColor" stroke-width="1.8"/>
      </svg>
    `;
  }

  return `
    <svg class="visibility-icon" viewBox="0 0 24 24" aria-hidden="true" focusable="false">
      <path d="M3.1 5.1 20.9 18.9" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round"/>
      <path d="M9.4 6.2A9.4 9.4 0 0 1 12 5.8c6 0 9.6 6.2 9.6 6.2a17 17 0 0 1-2.8 3.3" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
      <path d="M14.2 17.9a9.3 9.3 0 0 1-2.2.3C6 18.2 2.4 12 2.4 12a17.3 17.3 0 0 1 3.5-3.9" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round"/>
      <path d="M9.9 10.1a3.1 3.1 0 0 0 4 4" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round"/>
    </svg>
  `;
}

function buildWebsocketChip() {
  const statusClass =
    state.websocketState === "open"
      ? "status-online"
      : state.websocketState === "error"
        ? "status-danger"
        : state.websocketState === "closed"
          ? "status-warning"
          : "status-warning";

  const label =
    state.websocketState === "open"
      ? "Live"
      : state.websocketState === "connecting"
        ? "Connecting"
        : state.websocketState === "closed"
          ? "Retrying"
          : state.websocketState === "error"
            ? "Error"
            : "Idle";

  const detail = state.websocketError || state.bootstrap?.websocketUrl || "No websocket";
  return `<span class="chip ${statusClass}" data-role="socket-chip"><strong>Stream</strong> ${escapeHtml(label)}: ${escapeHtml(detail)}</span>`;
}

function renderStreamSnapshotStatus() {
  const partitions = (state.snapshot?.partitions || []).length;
  if (state.streamState === "loading") {
    return `streaming ${state.streamLoadedPartitions || partitions} partitions`;
  }
  if (state.streamState === "error") {
    return `stream error`;
  }
  return `${partitions} partitions`;
}

function updateSpeedUi() {
  if (cameraSpeedInput) {
    cameraSpeedInput.value = formatSpeed(inputController.getMoveSpeedMultiplier());
  }
}

function formatSpeed(value) {
  return Number(value || 1).toFixed(1);
}

function clampNumber(value, min, max, fallback) {
  const number = Number(value);
  if (!Number.isFinite(number)) {
    return fallback;
  }
  return Math.min(max, Math.max(min, Math.round(number)));
}

function formatPerfNumber(value, digits) {
  const number = Number(value || 0);
  if (digits > 0) {
    return number.toFixed(digits);
  }
  return number.toLocaleString();
}

function formatRenderScale(value) {
  const scale = Number(value || 1);
  return `${scale.toFixed(2).replace(/\.?0+$/, "")}x`;
}

function formatStride(value) {
  const stride = Math.max(1, Number(value || 1));
  return `${stride}x sample`;
}

function getLayerSummary() {
  const hidden = state.hiddenGizmoCategories.size;
  return hidden ? `${hidden} hidden` : "all visible";
}

function capitalize(value) {
  const text = String(value || "");
  return text.charAt(0).toUpperCase() + text.slice(1);
}

function updateClipUi() {
  const clip = renderer.getCameraClip();
  if (cameraNearInput) {
    cameraNearInput.value = formatClip(clip.near);
  }
  if (cameraFarInput) {
    cameraFarInput.value = formatClip(clip.far);
  }
}

function formatClip(value) {
  const number = Number(value || 0);
  return number >= 100 ? number.toFixed(0) : number.toFixed(2).replace(/\.?0+$/, "");
}

function formatObjectName(object) {
  const name = String(object?.name || "").trim();
  if (name) {
    return name;
  }

  const archetype = String(object?.archetype || "").trim();
  if (archetype) {
    return archetype;
  }

  const hasHeightfield = (object?.colliders || []).some((collider) => collider?.heightfield);
  return hasHeightfield ? "Terrain / Heightfield" : "World Object";
}

function resolveEnvironmentMode() {
  const configured = String(state.bootstrap?.environmentMode || "").trim().toUpperCase();
  if (configured === "2D" || configured === "3D") {
    return configured;
  }

  if (state.bootstrap?.snapshot || (state.bootstrap?.worlds || []).length > 0) {
    return "3D";
  }

  return "Unknown";
}

function renderDetail(label, value) {
  return `
    <div class="detail-item">
      <div class="detail-label">${escapeHtml(label)}</div>
      <div class="detail-value mono">${escapeHtml(value)}</div>
    </div>
  `;
}

function formatVector(vector) {
  if (!vector) {
    return "-";
  }
  return `${Number(vector.x || 0).toFixed(2)}, ${Number(vector.y || 0).toFixed(2)}, ${Number(vector.z || 0).toFixed(2)}`;
}

function escapeHtml(value) {
  return String(value ?? "")
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;")
    .replaceAll('"', "&quot;")
    .replaceAll("'", "&#39;");
}

function escapeAttribute(value) {
  return escapeHtml(value).replaceAll("\n", "&#10;");
}
