import { WorldInputController } from "./worldInputController.js";
import { WorldRenderer } from "./worldRenderer.js";

const vscode = acquireVsCodeApi();
const root = document.getElementById("world-root");

const state = {
  bootstrap: null,
  snapshot: null,
  error: "",
  selectedObjectId: null,
  filter: "",
  hiddenGizmoIds: new Set(),
  hiddenGizmoCategories: new Set(),
  websocketState: "idle",
  websocketError: "",
  reconnectTimer: null,
  shouldReconnect: false,
};

const inputController = new WorldInputController();
const renderer = new WorldRenderer(inputController);
let worldSelect = null;
let objectSearch = null;
let objectList = null;
let gizmoList = null;
let inspector = null;
let viewport = null;
let ws = null;

window.addEventListener("message", (event) => {
  const message = event.data;

  switch (message?.type) {
    case "world:bootstrap":
      state.bootstrap = message.payload || null;
      state.snapshot = message.payload?.snapshot || null;
      state.error = "";
      if (!state.selectedObjectId || !findObject(state.selectedObjectId)) {
        state.selectedObjectId = getObjects()[0]?.instanceId || null;
      }
      renderShell();
      refreshWorld();
      connectWebSocket();
      return;
    case "world:snapshot":
      state.snapshot = message.payload?.snapshot || null;
      if (message.payload?.selectedWorldIndex != null) {
        state.bootstrap = {
          ...(state.bootstrap || {}),
          selectedWorldIndex: message.payload.selectedWorldIndex,
        };
      }
      if (!findObject(state.selectedObjectId)) {
        state.selectedObjectId = getObjects()[0]?.instanceId || null;
      }
      refreshWorld();
      return;
    case "world:error":
      state.error = message.error || "Unknown world viewer error.";
      renderShell();
      refreshWorld();
      return;
    default:
      return;
  }
});

window.addEventListener("beforeunload", () => {
  disconnectWebSocket();
  renderer.dispose();
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
    <div class="world-shell">
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
          ${websocketChip}
          <button id="refresh-world">Refresh</button>
        </div>
      </section>
      <section class="world-layout">
        <section class="viewport-panel">
          <div class="viewport-toolbar">
            <div class="chip-row">
              <span class="chip"><strong>Snapshot</strong> ${(state.snapshot?.partitions || []).length} partitions</span>
              <span class="chip"><strong>Objects</strong> ${getObjects().length}</span>
            </div>
            <div class="chip-row">
              <span class="chip">Look: RMB drag</span>
              <span class="chip">Move: WASD + Q/E</span>
              <span class="chip">Pan: MMB drag</span>
              <span class="chip">Orbit: Alt + LMB</span>
              <span class="chip">Zoom: wheel</span>
            </div>
          </div>
          <div id="viewport" class="viewport"></div>
        </section>
        <aside class="sidebar-panel">
          <section class="sidebar-section">
            <div class="section-title">
              <h2>Scene</h2>
            </div>
            <div class="detail-grid">
              ${renderDetail("Dashboard", state.bootstrap?.baseUrl || "-")}
              ${renderDetail("World", getSelectedWorldSummary()?.name || "-")}
              ${renderDetail("Generated", state.snapshot?.generatedAtUtc || "-")}
            </div>
          </section>
          <section class="sidebar-section">
            <div class="section-title">
              <h2>Selection</h2>
            </div>
            <div id="inspector" class="detail-grid"></div>
          </section>
          <section class="sidebar-section" style="min-height: 0;">
            <div class="section-title">
              <h2>Objects</h2>
              <span class="hint">${getObjects().length} visible</span>
            </div>
            <input id="object-search" class="search-input" placeholder="Filter by archetype, instance id or client id..." value="${escapeAttribute(state.filter)}" />
            <div id="object-list" class="object-list"></div>
          </section>
          <section class="sidebar-section" style="min-height: 0;">
            <div class="section-title">
              <h2>Gizmos</h2>
              <span class="hint">${getGizmos().length} registered</span>
            </div>
            <div id="gizmo-list" class="object-list"></div>
          </section>
          <section class="sidebar-section">
            <div class="hint">Click an object row or click inside the scene to inspect and focus it.</div>
          </section>
        </aside>
      </section>
    </div>
  `;

  worldSelect = document.getElementById("world-select");
  objectSearch = document.getElementById("object-search");
  objectList = document.getElementById("object-list");
  gizmoList = document.getElementById("gizmo-list");
  inspector = document.getElementById("inspector");
  viewport = document.getElementById("viewport");

  document.getElementById("refresh-world")?.addEventListener("click", () => {
    vscode.postMessage({ type: "world:refresh" });
  });

  worldSelect?.addEventListener("change", (event) => {
    const nextWorldIndex = Number(event.target.value);
    vscode.postMessage({ type: "world:select", worldIndex: nextWorldIndex });
  });

  objectSearch?.addEventListener("input", (event) => {
    state.filter = event.target.value || "";
    renderObjectList();
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
    disconnectWebSocket();
    return;
  }

  renderInspector();
  renderObjectList();
  renderGizmoList();
}

function refreshWorld() {
  if (!state.bootstrap || !viewport || resolveEnvironmentMode() !== "3D") {
    renderInspector();
    renderObjectList();
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
    return;
  }

  if (!state.snapshot) {
    renderer.dispose();
    viewport.innerHTML = `<div class="loading">Waiting for world snapshot...</div>`;
    renderInspector();
    renderObjectList();
    return;
  }

  if (!renderer.renderer || viewport.children.length === 0 || viewport.querySelector("canvas") === null) {
    renderer.mount(viewport);
    viewport.addEventListener("click", handleViewportClick);
  }

  renderer.setSnapshot(state.snapshot);
  renderer.setGizmoVisibility(state.hiddenGizmoIds, state.hiddenGizmoCategories);

  if (!findObject(state.selectedObjectId)) {
    state.selectedObjectId = getObjects()[0]?.instanceId || null;
  }

  renderer.setSelectedObject(state.selectedObjectId);
  if (state.selectedObjectId) {
    renderer.focusOnObject(state.selectedObjectId);
  }

  renderInspector();
  renderObjectList();
  renderGizmoList();
}

function renderObjectList() {
  if (!objectList) {
    return;
  }

  const query = String(state.filter || "").trim().toLowerCase();
  const objects = getObjects()
    .slice()
    .sort((left, right) => {
      const leftKey = `${left.archetype}|${left.instanceId}`;
      const rightKey = `${right.archetype}|${right.instanceId}`;
      return leftKey.localeCompare(rightKey);
    })
    .filter((object) => {
      if (!query) {
        return true;
      }

      return [object.archetype, object.instanceId, object.clientId, object.zoneId]
        .filter(Boolean)
        .some((value) => String(value).toLowerCase().includes(query));
    });

  objectList.innerHTML = objects.length
    ? objects
        .map(
          (object) => `
            <button class="object-row ${object.instanceId === state.selectedObjectId ? "is-selected" : ""}" data-object-id="${escapeAttribute(object.instanceId)}">
              <div class="object-name">
                <strong>${escapeHtml(object.archetype || "Object")}</strong>
                ${object.clientId ? `<span class="chip">${escapeHtml(object.clientId)}</span>` : ""}
              </div>
              <div class="object-meta">${escapeHtml(object.instanceId)}</div>
              <div class="object-meta">Position: ${formatVector(object.transform?.position)}</div>
            </button>
          `
        )
        .join("")
    : `<div class="empty-state">No objects matched the current filter.</div>`;

  for (const button of objectList.querySelectorAll("[data-object-id]")) {
    button.addEventListener("click", () => {
      selectObject(button.dataset.objectId, true);
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
            <div class="object-meta">${escapeHtml(gizmo.type || "shape")} · ${escapeHtml(gizmo.source || "-")}</div>
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
    ${renderDetail("Archetype", object.archetype || "Object")}
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

function handleViewportClick(event) {
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

  if (envelope?.type !== "DashboardWorldObjectStatePacket" || !envelope.message) {
    return;
  }

  const selectedWorldIndex = state.bootstrap?.selectedWorldIndex;
  if (selectedWorldIndex == null || envelope.message.worldIndex !== selectedWorldIndex) {
    return;
  }

  renderer.applyRealtimePacket(envelope.message);
  renderer.setSelectedObject(state.selectedObjectId);
  renderer.setGizmoVisibility(state.hiddenGizmoIds, state.hiddenGizmoCategories);
  renderInspector();
  renderObjectList();
  renderGizmoList();
}

function getObjects() {
  return renderer.getObjects();
}

function getGizmos() {
  return renderer.getGizmos();
}

function findObject(instanceId) {
  if (!instanceId) {
    return null;
  }
  return renderer.getObject(instanceId);
}

function getSelectedWorldSummary() {
  const worlds = state.bootstrap?.worlds || [];
  return worlds.find((world) => world.index === state.bootstrap?.selectedWorldIndex) || null;
}

function rerenderToolbarOnly() {
  const chip = buildWebsocketChip();
  const existing = root.querySelector(".toolbar-actions");
  if (!existing) {
    renderShell();
    refreshWorld();
    return;
  }

  const chips = Array.from(existing.querySelectorAll(".chip"));
  const connectionChip = chips.find((item) => item.dataset.role === "socket-chip");
  if (connectionChip) {
    connectionChip.outerHTML = chip;
  } else {
    existing.insertAdjacentHTML("afterbegin", chip);
  }
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
