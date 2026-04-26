(function () {
  const vscode = acquireVsCodeApi();
  const app = document.getElementById("app");
  const kind = document.body.dataset.kind || "summary";

  const state = {
    kind,
    payload: null,
    error: "",
    baseUrl: "",
    filter: "",
    selectedVaultTypeKey: "",
    vaultPage: null,
  };

  window.addEventListener("message", (event) => {
    const message = event.data;
    switch (message?.type) {
      case "panel:data":
        state.kind = message.kind;
        state.baseUrl = message.baseUrl || "";
        state.payload = message.payload ?? null;
        state.error = "";

        if (state.kind === "vault") {
          const definitions = state.payload?.definitions || [];
          if (!state.selectedVaultTypeKey) {
            state.selectedVaultTypeKey =
              state.payload?.firstItems?.typeKey || definitions[0]?.typeKey || "";
          }
          if (state.payload?.firstItems) {
            state.vaultPage = state.payload.firstItems;
          }
        }

        render();
        return;
      case "panel:error":
        state.error = message.error || "Unknown dashboard error.";
        render();
        return;
      case "vault:items":
        if (message.typeKey === state.selectedVaultTypeKey) {
          state.vaultPage = message.payload;
          state.error = "";
          render();
        }
        return;
      default:
        return;
    }
  });

  app.addEventListener("click", async (event) => {
    const button = event.target.closest("[data-action]");
    if (!button) {
      return;
    }

    const action = button.dataset.action;

    switch (action) {
      case "refresh":
        vscode.postMessage({ type: "panel:refresh" });
        return;
      case "edit-config":
        await editConfig(button.dataset.key, button.dataset.value);
        return;
      case "close-connection":
        confirmAndSend(
          `Disconnect connection ${button.dataset.connectionId}?`,
          {
            type: "sessions:closeConnection",
            connectionId: button.dataset.connectionId,
          }
        );
        return;
      case "delete-room":
        confirmAndSend(`Delete room ${button.dataset.roomId} and disconnect every member?`, {
          type: "sessions:deleteRoom",
          roomId: button.dataset.roomId,
        });
        return;
      case "remove-room-connection":
        confirmAndSend(
          `Remove connection ${button.dataset.connectionId} from room ${button.dataset.roomId}?`,
          {
            type: "sessions:removeRoomConnection",
            roomId: button.dataset.roomId,
            connectionId: button.dataset.connectionId,
          }
        );
        return;
      case "copy-json":
        await navigator.clipboard.writeText(button.dataset.value || "");
        return;
      case "edit-cache":
        await editCacheEntry(Number(button.dataset.index || "-1"));
        return;
      case "delete-cache":
        confirmAndSend(
          `Delete cache entry ${button.dataset.key}?`,
          {
            type: "cache:delete",
            entry: getCacheEntry(Number(button.dataset.index || "-1")),
          }
        );
        return;
      case "vault-prev":
        loadVaultPage(
          Math.max(0, Number(button.dataset.skip || "0") - Number(button.dataset.take || "50"))
        );
        return;
      case "vault-next":
        loadVaultPage(Number(button.dataset.skip || "0") + Number(button.dataset.take || "50"));
        return;
      case "vault-edit-row":
        await editVaultRow(Number(button.dataset.index || "-1"));
        return;
      default:
        return;
    }
  });

  app.addEventListener("input", (event) => {
    const target = event.target;
    if (!(target instanceof HTMLInputElement)) {
      return;
    }

    if (target.dataset.role === "filter") {
      state.filter = target.value || "";
      render();
    }
  });

  app.addEventListener("change", (event) => {
    const target = event.target;
    if (!(target instanceof HTMLSelectElement)) {
      return;
    }

    if (target.dataset.role === "vault-select") {
      state.selectedVaultTypeKey = target.value;
      loadVaultPage(0);
    }
  });

  vscode.postMessage({ type: "panel:ready" });
  render();

  function render() {
    if (state.error) {
      app.innerHTML = `
        ${renderToolbar()}
        <div class="error-state">
          <div>
            <h2>Dashboard request failed</h2>
            <p>${escapeHtml(state.error)}</p>
          </div>
        </div>
      `;
      return;
    }

    if (!state.payload) {
      app.innerHTML = `
        ${renderToolbar()}
        <div class="loading">Waiting for dashboard data...</div>
      `;
      return;
    }

    app.innerHTML = `${renderToolbar()}${renderBody()}`;
  }

  function renderToolbar() {
    const labels = {
      summary: "Summary",
      config: "Config",
      sessions: "Sessions",
      cache: "Cache",
      vault: "Vault",
    };

    return `
      <section class="toolbar">
        <div class="toolbar-main">
          <div class="eyebrow">Altruist Dashboard</div>
          <div class="title-row">
            <h1>${escapeHtml(labels[state.kind] || "Dashboard")}</h1>
            ${state.baseUrl ? `<span class="badge">${escapeHtml(state.baseUrl)}</span>` : ""}
          </div>
          <div class="muted">Native VS Code client for the running Altruist dashboard backend.</div>
        </div>
        <div class="toolbar-actions">
          <button class="secondary" data-action="refresh">Refresh</button>
        </div>
      </section>
    `;
  }

  function renderBody() {
    switch (state.kind) {
      case "summary":
        return renderSummary();
      case "config":
        return renderConfig();
      case "sessions":
        return renderSessions();
      case "cache":
        return renderCache();
      case "vault":
        return renderVault();
      default:
        return `<div class="empty-state">Unsupported Altruist panel.</div>`;
    }
  }

  function renderSummary() {
    const payload = state.payload || {};
    const services = payload.services || [];
    const engine = payload.engine || null;
    const configs = payload.configs || [];

    return `
      <div class="stack">
        <section class="card">
          <h2>Overview</h2>
          <div class="stats-grid">
            <div class="stat">
              <div class="stat-label">Configs</div>
              <div class="stat-value">${configs.length}</div>
            </div>
            <div class="stat">
              <div class="stat-label">Services</div>
              <div class="stat-value">${Number(payload.serviceCount || services.length)}</div>
            </div>
            <div class="stat">
              <div class="stat-label">Diagnostics</div>
              <div class="stat-value">${engine?.diagnostics ? "On" : "Off"}</div>
            </div>
            <div class="stat">
              <div class="stat-label">Framerate</div>
              <div class="stat-value">${engine?.framerateHz ?? "-"}</div>
            </div>
          </div>
        </section>
        <section class="split-detail">
          <section class="card">
            <h2>Engine</h2>
            ${
              engine
                ? `
                  <div class="detail-list">
                    ${renderDetail("Diagnostics", String(engine.diagnostics))}
                    ${renderDetail("Framerate", String(engine.framerateHz))}
                    ${renderDetail("Unit", engine.unit || "-")}
                    ${renderDetail("Throttle", engine.throttle == null ? "-" : String(engine.throttle))}
                    ${renderDetail("Gravity", engine.gravity ? `${engine.gravity.x}, ${engine.gravity.y}, ${engine.gravity.z}` : "-")}
                  </div>
                `
                : `<div class="empty-state">No engine metadata was returned by the dashboard.</div>`
            }
          </section>
          <section class="card">
            <h2>Active Config Keys</h2>
            <div class="detail-list">
              ${configs.slice(0, 10).map((entry) => renderDetail(entry.key, stringifyValue(entry.value))).join("")}
            </div>
          </section>
        </section>
        <section class="card">
          <h2>Registered Services</h2>
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Name</th>
                  <th>Category</th>
                  <th>Assembly</th>
                  <th>Details</th>
                </tr>
              </thead>
              <tbody>
                ${
                  services.length
                    ? services
                        .map(
                          (service) => `
                            <tr>
                              <td>
                                <strong>${escapeHtml(service.name)}</strong>
                                <div class="muted mono">${escapeHtml(service.fullName)}</div>
                              </td>
                              <td>${escapeHtml(String(service.category))}</td>
                              <td>${escapeHtml(service.assembly || "-")}</td>
                              <td class="mono">
                                ${escapeHtml(
                                  [
                                    service.lifetime ? `lifetime=${service.lifetime}` : "",
                                    service.serviceType ? `service=${service.serviceType}` : "",
                                    service.endpoint ? `endpoint=${service.endpoint}` : "",
                                    service.context ? `context=${service.context}` : "",
                                  ]
                                    .filter(Boolean)
                                    .join(" | ") || "-"
                                )}
                              </td>
                            </tr>
                          `
                        )
                        .join("")
                    : `<tr><td colspan="4" class="muted">No services reported by the dashboard.</td></tr>`
                }
              </tbody>
            </table>
          </div>
        </section>
      </div>
    `;
  }

  function renderConfig() {
    const configs = filterEntries(state.payload?.configs || [], state.filter, ["key", "value"]);

    return `
      <div class="stack">
        <section class="card">
          <h2>Runtime Config</h2>
          <div class="filters">
            <input class="grow" data-role="filter" value="${escapeAttribute(state.filter)}" placeholder="Filter config keys or values..." />
          </div>
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Key</th>
                  <th>Value</th>
                  <th>Mode</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                ${
                  configs.length
                    ? configs
                        .map(
                          (entry) => `
                            <tr>
                              <td class="mono">${escapeHtml(entry.key)}</td>
                              <td class="mono">${escapeHtml(stringifyValue(entry.value))}</td>
                              <td>${entry.modifiable ? `<span class="badge">Live</span>` : "Read-only"}</td>
                              <td>
                                ${
                                  entry.modifiable
                                    ? `<button data-action="edit-config" data-key="${escapeAttribute(entry.key)}" data-value="${escapeAttribute(stringifyValue(entry.value))}">Edit</button>`
                                    : `<span class="muted">No action</span>`
                                }
                              </td>
                            </tr>
                          `
                        )
                        .join("")
                    : `<tr><td colspan="4" class="muted">No config entries matched your filter.</td></tr>`
                }
              </tbody>
            </table>
          </div>
        </section>
      </div>
    `;
  }

  function renderSessions() {
    const payload = state.payload || {};
    const rooms = payload.rooms || [];
    const connections = payload.connections || [];

    return `
      <div class="content-grid">
        <section class="card">
          <h2>Rooms</h2>
          <div class="list">
            ${
              rooms.length
                ? rooms
                    .map(
                      (room) => `
                        <article class="list-item">
                          <div class="list-item-header">
                            <div>
                              <strong>${escapeHtml(room.roomId)}</strong>
                              <div class="muted">${Number(room.connectionCount || 0)} active connection(s)</div>
                            </div>
                            <div class="row-actions">
                              <button class="danger" data-action="delete-room" data-room-id="${escapeAttribute(room.roomId)}">Delete Room</button>
                            </div>
                          </div>
                          <div class="detail-list">
                            ${
                              (room.connections || []).length
                                ? room.connections
                                    .map(
                                      (conn) => `
                                        <div class="detail-item">
                                          <div class="detail-label">Connection</div>
                                          <div class="detail-value mono">${escapeHtml(conn.connectionId)}</div>
                                          <div class="row-actions">
                                            <button data-action="remove-room-connection" data-room-id="${escapeAttribute(room.roomId)}" data-connection-id="${escapeAttribute(conn.connectionId)}">Disconnect</button>
                                          </div>
                                        </div>
                                      `
                                    )
                                    .join("")
                                : `<div class="muted">No connections in this room.</div>`
                            }
                          </div>
                        </article>
                      `
                    )
                    .join("")
                : `<div class="empty-state">No rooms are currently active.</div>`
            }
          </div>
        </section>
        <section class="card">
          <h2>All Connections</h2>
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Connection</th>
                  <th>Room</th>
                  <th>IP</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                ${
                  connections.length
                    ? connections
                        .map(
                          (conn) => `
                            <tr>
                              <td class="mono">${escapeHtml(conn.connectionId)}</td>
                              <td>${escapeHtml(conn.roomId || "-")}</td>
                              <td class="mono">${escapeHtml(conn.ipAddress || "-")}</td>
                              <td><button class="danger" data-action="close-connection" data-connection-id="${escapeAttribute(conn.connectionId)}">Disconnect</button></td>
                            </tr>
                          `
                        )
                        .join("")
                    : `<tr><td colspan="4" class="muted">No active connections were returned.</td></tr>`
                }
              </tbody>
            </table>
          </div>
        </section>
      </div>
    `;
  }

  function renderCache() {
    const payload = state.payload || {};
    const info = payload.info || {};
    const entries = filterEntries(payload.entries || [], state.filter, [
      "typeShortName",
      "key",
      "groupId",
      "source",
      "preview",
    ]);

    return `
      <div class="stack">
        <section class="card">
          <h2>Cache Provider</h2>
          <div class="stats-grid">
            <div class="stat">
              <div class="stat-label">Provider</div>
              <div class="stat-value">${escapeHtml(info.provider || "-")}</div>
            </div>
            <div class="stat">
              <div class="stat-label">Connected</div>
              <div class="stat-value">${info.isConnected ? "Yes" : "No"}</div>
            </div>
            <div class="stat">
              <div class="stat-label">In-Memory Entries</div>
              <div class="stat-value">${Number(info.inMemoryEntryCount || 0)}</div>
            </div>
          </div>
        </section>
        <section class="card">
          <h2>Entries</h2>
          <div class="filters">
            <input class="grow" data-role="filter" value="${escapeAttribute(state.filter)}" placeholder="Filter by key, type, group or source..." />
          </div>
          <div class="table-wrap">
            <table>
              <thead>
                <tr>
                  <th>Type</th>
                  <th>Key</th>
                  <th>Group</th>
                  <th>Source</th>
                  <th>Preview</th>
                  <th>Actions</th>
                </tr>
              </thead>
              <tbody>
                ${
                  entries.length
                    ? entries
                        .map((entry) => {
                          const index = (payload.entries || []).indexOf(entry);
                          return `
                            <tr>
                              <td>
                                <strong>${escapeHtml(entry.typeShortName || entry.type || "-")}</strong>
                                <div class="muted mono">${escapeHtml(entry.type || "-")}</div>
                              </td>
                              <td class="mono">${escapeHtml(entry.key || "-")}</td>
                              <td class="mono">${escapeHtml(entry.groupId || "-")}</td>
                              <td>${escapeHtml(entry.source || "-")}</td>
                              <td><div class="pre mono">${escapeHtml(entry.preview || JSON.stringify(entry.value, null, 2))}</div></td>
                              <td>
                                <div class="row-actions">
                                  <button class="secondary" data-action="copy-json" data-value="${escapeAttribute(JSON.stringify(entry.value, null, 2))}">Copy</button>
                                  <button data-action="edit-cache" data-index="${index}">Edit</button>
                                  <button class="danger" data-action="delete-cache" data-index="${index}" data-key="${escapeAttribute(entry.key || "")}">Delete</button>
                                </div>
                              </td>
                            </tr>
                          `;
                        })
                        .join("")
                    : `<tr><td colspan="6" class="muted">No cache entries matched your filter.</td></tr>`
                }
              </tbody>
            </table>
          </div>
        </section>
      </div>
    `;
  }

  function renderVault() {
    const definitions = state.payload?.definitions || [];
    const page = state.vaultPage || state.payload?.firstItems || null;

    return `
      <div class="stack">
        <section class="card">
          <h2>Vault Definitions</h2>
          <div class="filters">
            <select data-role="vault-select" class="grow">
              ${definitions
                .map(
                  (definition) => `
                    <option value="${escapeAttribute(definition.typeKey)}" ${definition.typeKey === state.selectedVaultTypeKey ? "selected" : ""}>
                      ${escapeHtml(definition.typeKey)} (${escapeHtml(definition.keyspace)}.${escapeHtml(definition.tableName)})
                    </option>
                  `
                )
                .join("")}
            </select>
          </div>
          ${
            definitions.length
              ? renderSelectedVaultDefinition(definitions.find((item) => item.typeKey === state.selectedVaultTypeKey) || definitions[0])
              : `<div class="empty-state">No vault metadata was returned.</div>`
          }
        </section>
        <section class="card">
          <div class="list-item-header">
            <div>
              <h2>Vault Items</h2>
              <div class="muted">Edit a row and the extension will send it back through the dashboard batch update endpoint.</div>
            </div>
            ${
              page
                ? `
                  <div class="row-actions">
                    <button class="secondary" data-action="vault-prev" data-skip="${page.skip}" data-take="${page.take}" ${page.skip <= 0 ? "disabled" : ""}>Previous</button>
                    <button class="secondary" data-action="vault-next" data-skip="${page.skip}" data-take="${page.take}" ${(page.skip + page.take) >= page.total ? "disabled" : ""}>Next</button>
                  </div>
                `
                : ""
            }
          </div>
          ${
            page
              ? `
                <div class="chip-row" style="margin-bottom: 14px;">
                  <span class="chip"><strong>Total</strong> ${Number(page.total || 0)}</span>
                  <span class="chip"><strong>Skip</strong> ${Number(page.skip || 0)}</span>
                  <span class="chip"><strong>Take</strong> ${Number(page.take || 0)}</span>
                </div>
                <div class="table-wrap">
                  <table>
                    <thead>
                      <tr>
                        ${(page.fields || []).map((field) => `<th>${escapeHtml(field)}</th>`).join("")}
                        <th>Actions</th>
                      </tr>
                    </thead>
                    <tbody>
                      ${
                        (page.items || []).length
                          ? page.items
                              .map(
                                (row, index) => `
                                  <tr>
                                    ${(page.fields || [])
                                      .map((field) => `<td class="mono">${escapeHtml(stringifyValue(row[field]))}</td>`)
                                      .join("")}
                                    <td><button data-action="vault-edit-row" data-index="${index}">Edit Row</button></td>
                                  </tr>
                                `
                              )
                              .join("")
                          : `<tr><td colspan="${(page.fields || []).length + 1}" class="muted">This vault page is empty.</td></tr>`
                      }
                    </tbody>
                  </table>
                </div>
              `
              : `<div class="empty-state">Choose a vault definition to load its first page.</div>`
          }
        </section>
      </div>
    `;
  }

  function renderSelectedVaultDefinition(definition) {
    return `
      <div class="split-detail">
        <div class="detail-list">
          ${renderDetail("CLR Type", definition.clrTypeShort || definition.clrType || "-")}
          ${renderDetail("Type Key", definition.typeKey || "-")}
          ${renderDetail("Keyspace", definition.keyspace || "-")}
          ${renderDetail("Table", definition.tableName || "-")}
          ${renderDetail("History", definition.storeHistory ? "Enabled" : "Disabled")}
        </div>
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Field</th>
                <th>Type</th>
                <th>Flags</th>
              </tr>
            </thead>
            <tbody>
              ${(definition.columns || [])
                .map(
                  (column) => `
                    <tr>
                      <td class="mono">${escapeHtml(column.fieldName)}</td>
                      <td class="mono">${escapeHtml(column.clrType || "-")}</td>
                      <td class="mono">${escapeHtml(
                        [
                          column.isPrimaryKey ? "PK" : "",
                          column.isIndexed ? "IDX" : "",
                          column.isUnique ? "UNIQUE" : "",
                          column.isForeignKey ? "FK" : "",
                          column.isNullable ? "NULL" : "NOT NULL",
                        ]
                          .filter(Boolean)
                          .join(" | ")
                      )}</td>
                    </tr>
                  `
                )
                .join("")}
            </tbody>
          </table>
        </div>
      </div>
    `;
  }

  function renderDetail(label, value) {
    return `
      <div class="detail-item">
        <div class="detail-label">${escapeHtml(label)}</div>
        <div class="detail-value mono">${escapeHtml(value)}</div>
      </div>
    `;
  }

  function filterEntries(entries, filter, keys) {
    const query = String(filter || "").trim().toLowerCase();
    if (!query) {
      return entries;
    }

    return entries.filter((entry) =>
      keys.some((key) => stringifyValue(entry[key]).toLowerCase().includes(query))
    );
  }

  function getCacheEntry(index) {
    return state.payload?.entries?.[index] || null;
  }

  async function editConfig(key, value) {
    if (!key) {
      return;
    }

    const nextValue = window.prompt(`New value for ${key}`, value ?? "");
    if (nextValue === null) {
      return;
    }

    vscode.postMessage({
      type: "config:update",
      key,
      value: nextValue,
    });
  }

  async function editCacheEntry(index) {
    const entry = getCacheEntry(index);
    if (!entry) {
      return;
    }

    const raw = window.prompt(
      `Edit cache entry ${entry.key} (${entry.typeShortName || entry.type}) as JSON`,
      JSON.stringify(entry.value, null, 2)
    );

    if (raw === null) {
      return;
    }

    let parsed;
    try {
      parsed = JSON.parse(raw);
    } catch (error) {
      window.alert(`Invalid JSON: ${error instanceof Error ? error.message : String(error)}`);
      return;
    }

    vscode.postMessage({
      type: "cache:update",
      entry: {
        type: entry.type,
        groupId: entry.groupId ?? "",
        key: entry.key,
        value: parsed,
      },
    });
  }

  async function editVaultRow(index) {
    const page = state.vaultPage;
    const row = page?.items?.[index];
    if (!page || !row || !state.selectedVaultTypeKey) {
      return;
    }

    const raw = window.prompt(
      `Edit vault row for ${state.selectedVaultTypeKey} as JSON`,
      JSON.stringify(row, null, 2)
    );

    if (raw === null) {
      return;
    }

    let parsed;
    try {
      parsed = JSON.parse(raw);
    } catch (error) {
      window.alert(`Invalid JSON: ${error instanceof Error ? error.message : String(error)}`);
      return;
    }

    vscode.postMessage({
      type: "vault:batchUpdate",
      typeKey: state.selectedVaultTypeKey,
      items: [parsed],
    });
  }

  function loadVaultPage(skip) {
    if (!state.selectedVaultTypeKey) {
      return;
    }

    const take = state.vaultPage?.take || 50;
    vscode.postMessage({
      type: "vault:loadItems",
      typeKey: state.selectedVaultTypeKey,
      skip,
      take,
    });
  }

  function confirmAndSend(message, payload) {
    if (!window.confirm(message)) {
      return;
    }

    vscode.postMessage(payload);
  }

  function stringifyValue(value) {
    if (value == null) {
      return "";
    }

    if (typeof value === "string") {
      return value;
    }

    try {
      return JSON.stringify(value);
    } catch {
      return String(value);
    }
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
})();
