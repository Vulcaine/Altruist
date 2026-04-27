(function () {
  const vscode = acquireVsCodeApi();
  const app = document.getElementById("app");
  const kind = document.body.dataset.kind || "summary";

  const state = {
    kind,
    payload: null,
    error: "",
    baseUrl: "",
    isBootstrapping: true,
    isRefreshing: false,
    loadingLabel: "Loading dashboard data...",
    configFilter: "",
    cacheFilter: "",
    serviceFilter: "",
    selectedVaultTypeKey: "",
    vaultPage: null,
    vaultOriginalItems: [],
    vaultQuerySql: "",
    vaultQueryResult: null,
    vaultSortField: "",
    vaultSortDirection: "asc",
    vaultFilters: {},
    vaultColumnWidths: {},
    expandedVaultCells: {},
    modal: null,
    dirtyConfig: {},
    dirtyVaultRows: {},
  };
  let activeColumnResize = null;

  window.addEventListener("message", (event) => {
    const message = event.data;
    switch (message?.type) {
      case "panel:loading":
        state.isBootstrapping = !state.payload;
        state.isRefreshing = !!state.payload;
        state.loadingLabel = message.label || "Loading dashboard data...";
        render();
        return;
      case "panel:data":
        state.kind = message.kind;
        state.baseUrl = message.baseUrl || "";
        state.payload = message.payload ?? null;
        state.error = "";
        state.isBootstrapping = false;
        state.isRefreshing = false;
        state.dirtyConfig = reconcileDirtyConfig(state.dirtyConfig, state.payload?.configs || []);

        if (state.kind === "vault") {
          const definitions = state.payload?.definitions || [];
          const selectedTypeKey =
            state.selectedVaultTypeKey ||
            state.payload?.firstItems?.typeKey ||
            definitions[0]?.typeKey ||
            "";
          state.selectedVaultTypeKey = selectedTypeKey;

          if (state.payload?.firstItems?.typeKey === selectedTypeKey) {
            applyVaultPage(state.payload.firstItems);
          } else if (!state.vaultPage && selectedTypeKey) {
            requestVaultPage(0, getVaultTake());
          }
        }

        render();
        return;
      case "panel:error":
        state.error = message.error || "Unknown dashboard error.";
        state.isBootstrapping = false;
        state.isRefreshing = false;
        render();
        return;
      case "vault:items":
        if (message.typeKey === state.selectedVaultTypeKey) {
          applyVaultPage(message.payload);
          state.error = "";
          state.isBootstrapping = false;
          state.isRefreshing = false;
          render();
        }
        return;
      case "vault:queryResult":
        if (message.typeKey === state.selectedVaultTypeKey) {
          state.vaultQueryResult = message.payload ?? null;
          state.error = "";
          state.isBootstrapping = false;
          state.isRefreshing = false;
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
        beginLoading("Refreshing dashboard...");
        vscode.postMessage({
          type: "panel:refresh",
          state: buildPanelRefreshState(),
        });
        return;
      case "edit-config":
        openConfigModal(button.dataset.key || "");
        return;
      case "stage-config-change":
        stageConfigModal();
        return;
      case "save-config-batch":
        commitConfigChanges();
        return;
      case "discard-config-batch":
        discardConfigChanges();
        return;
      case "close-connection":
        confirmAndSend(
          `Disconnect connection ${button.dataset.connectionId}?`,
          {
            type: "sessions:closeConnection",
            connectionId: button.dataset.connectionId,
          },
          "Disconnecting session..."
        );
        return;
      case "delete-room":
        confirmAndSend(
          `Delete room ${button.dataset.roomId} and disconnect every member?`,
          {
            type: "sessions:deleteRoom",
            roomId: button.dataset.roomId,
          },
          "Deleting room..."
        );
        return;
      case "remove-room-connection":
        confirmAndSend(
          `Remove connection ${button.dataset.connectionId} from room ${button.dataset.roomId}?`,
          {
            type: "sessions:removeRoomConnection",
            roomId: button.dataset.roomId,
            connectionId: button.dataset.connectionId,
          },
          "Disconnecting room member..."
        );
        return;
      case "copy-json":
        await navigator.clipboard.writeText(button.dataset.value || "");
        return;
      case "edit-cache":
        openCacheModal(Number(button.dataset.index || "-1"));
        return;
      case "save-cache-entry":
        saveCacheModal();
        return;
      case "delete-cache":
        confirmAndSend(
          `Delete cache entry ${button.dataset.key}?`,
          {
            type: "cache:delete",
            entry: getCacheEntry(Number(button.dataset.index || "-1")),
          },
          "Deleting cache entry..."
        );
        return;
      case "vault-prev":
        if (confirmVaultDiscard("Discard pending vault edits and change page?")) {
          requestVaultPage(
            Math.max(0, Number(button.dataset.skip || "0") - Number(button.dataset.take || "50")),
            Number(button.dataset.take || "50")
          );
        }
        return;
      case "vault-next":
        if (confirmVaultDiscard("Discard pending vault edits and change page?")) {
          requestVaultPage(
            Number(button.dataset.skip || "0") + Number(button.dataset.take || "50"),
            Number(button.dataset.take || "50")
          );
        }
        return;
      case "vault-edit-cell":
        openVaultCellModal(
          Number(button.dataset.rowIndex || "-1"),
          button.dataset.field || ""
        );
        return;
      case "vault-sort":
        toggleVaultSort(button.dataset.field || "");
        return;
      case "vault-filter":
        openVaultFilterModal(button.dataset.field || "");
        return;
      case "save-vault-cell":
        saveVaultCellModal();
        return;
      case "stage-vault-filter":
        stageVaultFilterModal();
        return;
      case "commit-vault-batch":
        commitVaultChanges();
        return;
      case "discard-vault-batch":
        discardVaultChanges();
        return;
      case "execute-vault-query":
        executeVaultQuery();
        return;
      case "clear-vault-query":
        state.vaultQueryResult = null;
        render();
        return;
      case "close-modal":
        closeModal();
        return;
      default:
        return;
    }
  });

  app.addEventListener("input", (event) => {
    const target = event.target;
    if (!(target instanceof HTMLInputElement || target instanceof HTMLTextAreaElement)) {
      return;
    }

    switch (target.dataset.role) {
      case "config-filter":
        state.configFilter = target.value || "";
        render();
        return;
      case "cache-filter":
        state.cacheFilter = target.value || "";
        render();
        return;
      case "service-filter":
        state.serviceFilter = target.value || "";
        render();
        return;
      case "vault-filter-inline":
        state.vaultFilters[target.dataset.field || ""] = target.value || "";
        render();
        return;
      case "vault-query-sql":
        state.vaultQuerySql = target.value || "";
        return;
      case "modal-input":
        if (state.modal) {
          state.modal.editValue = target.value || "";
          syncModalEditorPreview();
        }
        return;
      default:
        return;
    }
  });

  app.addEventListener("scroll", (event) => {
    const target = event.target;
    if (!(target instanceof HTMLTextAreaElement) || target.dataset.role !== "modal-input") {
      return;
    }

    syncModalEditorScroll(target);
  }, true);

  app.addEventListener("change", (event) => {
    const target = event.target;
    if (!(target instanceof HTMLSelectElement)) {
      return;
    }

    if (target.dataset.role === "vault-select") {
      if (!confirmVaultDiscard("Discard pending vault edits and switch vault?")) {
        render();
        return;
      }

      state.selectedVaultTypeKey = target.value;
      state.vaultQueryResult = null;
      state.vaultSortField = "";
      state.vaultSortDirection = "asc";
      state.vaultFilters = {};
      state.expandedVaultCells = {};
      requestVaultPage(0, getVaultTake());
    }
  });

  app.addEventListener("mousedown", (event) => {
    const handle = event.target.closest(".column-resizer");
    if (!handle) {
      return;
    }

    event.preventDefault();
    startColumnResize(handle.dataset.field || "", event.clientX);
  });

  app.addEventListener("dblclick", (event) => {
    const handle = event.target.closest(".column-resizer");
    if (handle) {
      event.preventDefault();
      autoSizeVaultColumn(handle.dataset.field || "");
      return;
    }

    const target = event.target.closest("[data-cell-key]");
    if (!target) {
      return;
    }

    toggleVaultCellExpansion(target.dataset.cellKey || "");
  });

  window.addEventListener("mousemove", (event) => {
    if (!activeColumnResize) {
      return;
    }

    const nextWidth = Math.max(90, activeColumnResize.startWidth + (event.clientX - activeColumnResize.startX));
    setVaultColumnWidth(activeColumnResize.field, nextWidth);
    render();
  });

  window.addEventListener("mouseup", () => {
    if (!activeColumnResize) {
      return;
    }

    activeColumnResize = null;
    document.body.classList.remove("is-col-resizing");
  });

  vscode.postMessage({ type: "panel:ready" });
  render();

  function render() {
    app.innerHTML = `
      ${renderToolbar()}
      ${renderBody()}
      ${renderOverlay()}
      ${renderModal()}
    `;
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
          ${renderIconButton("refresh", "Refresh", "refresh", "neutral")}
        </div>
      </section>
    `;
  }

  function renderBody() {
    if (state.error && !state.payload) {
      return `
        <div class="error-state">
          <div>
            <h2>Dashboard request failed</h2>
            <p>${escapeHtml(state.error)}</p>
          </div>
        </div>
      `;
    }

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

  function renderOverlay() {
    const visible = state.isBootstrapping || state.isRefreshing;
    if (!visible) {
      return "";
    }

    return `
      <div class="panel-overlay">
        <div class="spinner-card">
          <div class="spinner"></div>
          <div class="spinner-label">${escapeHtml(state.loadingLabel || "Loading...")}</div>
        </div>
      </div>
    `;
  }

  function renderModal() {
    if (!state.modal) {
      return "";
    }

    return `
      <div class="modal-backdrop">
        <div class="modal-card">
          <div class="modal-header">
            <div>
              <h2>${escapeHtml(state.modal.title)}</h2>
              ${state.modal.subtitle ? `<p class="muted">${escapeHtml(state.modal.subtitle)}</p>` : ""}
            </div>
            ${renderIconButton("close-modal", "Close", "close", "neutral")}
          </div>
          <div class="modal-body">
            ${
              state.modal.prettyValue
                ? `
                  <div class="modal-section">
                    <div class="detail-label">Current Value</div>
                    <pre class="json-view"><code>${renderHighlightedJson(state.modal.prettyValue)}</code></pre>
                  </div>
                `
                : ""
            }
            <div class="modal-section">
              <div class="detail-label">Edit</div>
              <div class="code-editor-shell">
                <pre class="json-view json-view--editor"><code>${renderHighlightedJson(prettyJsonOrString(state.modal.editValue || ""))}</code></pre>
                <textarea
                  class="modal-input modal-input--overlay"
                  data-role="modal-input"
                  spellcheck="false"
                >${escapeHtml(state.modal.editValue || "")}</textarea>
              </div>
            </div>
            ${state.modal.error ? `<div class="modal-error">${escapeHtml(state.modal.error)}</div>` : ""}
          </div>
          <div class="modal-footer">
            ${renderIconButton("close-modal", "Cancel", "cancel", "neutral")}
            ${renderIconButton(state.modal.saveAction, state.modal.saveLabel || "Save", "save", state.modal.saveTone || "success")}
          </div>
        </div>
      </div>
    `;
  }

  function renderSummary() {
    const payload = state.payload || {};
    const services = filterEntries(payload.services || [], state.serviceFilter, [
      "name",
      "fullName",
      "assembly",
      "endpoint",
      "context",
      "serviceType",
      "lifetime",
    ]);
    const engine = payload.engine || null;
    const configs = payload.configs || [];

    return `
      <div class="stack">
        ${renderInlineError()}
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
          <div class="section-header">
            <div>
              <h2>Registered Services</h2>
              <div class="muted">Search by type, lifetime, endpoint, assembly, or context.</div>
            </div>
            <div class="filters compact">
              <input
                class="grow"
                data-role="service-filter"
                value="${escapeAttribute(state.serviceFilter)}"
                placeholder="Filter services..."
              />
            </div>
          </div>
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
                    : `<tr><td colspan="4" class="muted">No services matched your filter.</td></tr>`
                }
              </tbody>
            </table>
          </div>
        </section>
      </div>
    `;
  }

  function renderConfig() {
    const configs = filterEntries(state.payload?.configs || [], state.configFilter, ["key", "value"]);
    const hasPending = getDirtyConfigEntries().length > 0;

    return `
      <div class="stack">
        ${renderInlineError()}
        <section class="card">
          <div class="section-header">
            <div>
              <h2>Runtime Config</h2>
              <div class="muted">Every live config value can be edited from here.</div>
            </div>
            <div class="row-actions">
              ${
                hasPending
                  ? `
                    ${renderIconButton("discard-config-batch", "Discard Config Changes", "cancel", "neutral")}
                    ${renderIconButton("save-config-batch", "Commit Config Changes", "save", "success")}
                  `
                  : ""
              }
            </div>
          </div>
          <div class="filters">
            <input class="grow" data-role="config-filter" value="${escapeAttribute(state.configFilter)}" placeholder="Filter config keys or values..." />
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
                        .map((entry) => {
                          const dirty = Object.prototype.hasOwnProperty.call(state.dirtyConfig, entry.key);
                          const displayValue = dirty ? state.dirtyConfig[entry.key] : stringifyValue(entry.value);
                          return `
                            <tr class="${dirty ? "row-dirty" : ""}">
                              <td class="mono">${escapeHtml(entry.key)}</td>
                              <td class="mono">${escapeHtml(displayValue)}</td>
                              <td>${entry.modifiable ? `<span class="badge">Live</span>` : "Read-only"}</td>
                              <td>
                                <div class="row-actions">
                                  ${
                                    entry.modifiable
                                      ? renderIconButton(
                                          "edit-config",
                                          `Edit ${entry.key}`,
                                          "edit",
                                          dirty ? "success" : "accent",
                                          {
                                            key: entry.key,
                                          }
                                        )
                                      : `<span class="muted">-</span>`
                                  }
                                </div>
                              </td>
                            </tr>
                          `;
                        })
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
        ${renderInlineError()}
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
                              ${renderIconButton("delete-room", `Delete Room ${room.roomId}`, "delete", "danger", {
                                roomId: room.roomId,
                              })}
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
                                            ${renderIconButton(
                                              "remove-room-connection",
                                              `Disconnect ${conn.connectionId}`,
                                              "disconnect",
                                              "danger",
                                              {
                                                roomId: room.roomId,
                                                connectionId: conn.connectionId,
                                              }
                                            )}
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
                              <td>
                                <div class="row-actions">
                                  ${renderIconButton("close-connection", `Disconnect ${conn.connectionId}`, "disconnect", "danger", {
                                    connectionId: conn.connectionId,
                                  })}
                                </div>
                              </td>
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
    const entries = filterEntries(payload.entries || [], state.cacheFilter, [
      "typeShortName",
      "key",
      "groupId",
      "source",
      "preview",
    ]);

    return `
      <div class="stack">
        ${renderInlineError()}
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
            <input class="grow" data-role="cache-filter" value="${escapeAttribute(state.cacheFilter)}" placeholder="Filter by key, type, group or source..." />
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
                                  ${renderIconButton("copy-json", `Copy ${entry.key}`, "copy", "neutral", {
                                    value: JSON.stringify(entry.value, null, 2),
                                  })}
                                  ${renderIconButton("edit-cache", `Edit ${entry.key}`, "edit", "accent", {
                                    index,
                                  })}
                                  ${renderIconButton("delete-cache", `Delete ${entry.key}`, "delete", "danger", {
                                    index,
                                    key: entry.key || "",
                                  })}
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
    const selected = definitions.find((item) => item.typeKey === state.selectedVaultTypeKey) || definitions[0] || null;
    const page = state.vaultPage;
    const hasPending = hasVaultChanges();

    return `
      <div class="stack">
        ${renderInlineError()}
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
            selected
              ? renderSelectedVaultDefinition(selected)
              : `<div class="empty-state">No vault metadata was returned.</div>`
          }
        </section>
        <section class="card">
          <div class="section-header">
            <div>
              <h2>Vault Items</h2>
              <div class="muted">Edit cells with the pencil action, then commit all changes in one batch.</div>
            </div>
            <div class="row-actions">
              ${
                hasPending
                  ? `
                    ${renderIconButton("discard-vault-batch", "Discard Vault Changes", "cancel", "neutral")}
                    ${renderIconButton("commit-vault-batch", "Commit Vault Changes", "save", "success")}
                  `
                  : ""
              }
              ${
                page
                  ? `
                    ${renderIconButton("vault-prev", "Previous Page", "prev", "neutral", {
                      skip: page.skip,
                      take: page.take,
                    }, page.skip <= 0)}
                    ${renderIconButton("vault-next", "Next Page", "next", "neutral", {
                      skip: page.skip,
                      take: page.take,
                    }, (page.skip + page.take) >= page.total)}
                  `
                  : ""
              }
            </div>
          </div>
          ${
            page
              ? `
                <div class="chip-row" style="margin-bottom: 14px;">
                  <span class="chip"><strong>Total</strong> ${Number(page.total || 0)}</span>
                  <span class="chip"><strong>Skip</strong> ${Number(page.skip || 0)}</span>
                  <span class="chip"><strong>Take</strong> ${Number(page.take || 0)}</span>
                </div>
              `
              : ""
          }
          ${
            selected?.supportsSqlQuery
              ? `
                <div class="query-panel">
                  <div class="section-header">
                    <div>
                      <h3>SQL Query</h3>
                      <div class="muted">Raw SQL against the selected Postgres-backed vault schema.</div>
                    </div>
                    <div class="row-actions">
                      ${renderIconButton("clear-vault-query", "Clear Query Result", "cancel", "neutral")}
                      ${renderIconButton("execute-vault-query", "Execute SQL", "execute", "accent")}
                    </div>
                  </div>
                  <textarea
                    class="query-input"
                    data-role="vault-query-sql"
                    spellcheck="false"
                    placeholder="SELECT * FROM ${escapeAttribute(selected.tableName)} LIMIT 20;"
                  >${escapeHtml(state.vaultQuerySql)}</textarea>
                  ${renderVaultQueryResult()}
                </div>
              `
              : `
                <div class="empty-note">Raw SQL query is available only for SQL-backed vaults.</div>
              `
          }
          ${
            page
              ? renderVaultItemsTable(page, selected)
              : `<div class="empty-state">Choose a vault definition to load its first page.</div>`
          }
        </section>
      </div>
    `;
  }

  function renderVaultQueryResult() {
    const result = state.vaultQueryResult;
    if (!result) {
      return "";
    }

    if (!result.hasRowset) {
      return `
        <div class="query-result-meta">
          <span class="chip"><strong>Statement</strong> ${escapeHtml(result.statementKind || "statement")}</span>
          <span class="chip"><strong>Affected</strong> ${Number(result.affectedRows || 0)}</span>
        </div>
      `;
    }

    return `
      <div class="query-result-meta">
        <span class="chip"><strong>Statement</strong> ${escapeHtml(result.statementKind || "query")}</span>
        <span class="chip"><strong>Rows</strong> ${(result.rows || []).length}</span>
      </div>
      <div class="table-wrap">
        <table>
          <thead>
            <tr>
              ${(result.columns || []).map((column) => `<th>${escapeHtml(column)}</th>`).join("")}
            </tr>
          </thead>
          <tbody>
            ${
              (result.rows || []).length
                ? result.rows
                    .map(
                      (row) => `
                        <tr>
                          ${(result.columns || [])
                            .map((column) => `<td class="mono">${renderValuePreview(row[column], false)}</td>`)
                            .join("")}
                        </tr>
                      `
                    )
                    .join("")
                : `<tr><td colspan="${(result.columns || []).length || 1}" class="muted">Query returned no rows.</td></tr>`
            }
          </tbody>
        </table>
      </div>
    `;
  }

  function renderVaultItemsTable(page, selected) {
    const fields = page.fields || [];
    const columns = orderedVaultColumns(selected, fields);
    const rows = getVaultDisplayRows(page, columns);

    return `
      <div class="table-wrap">
        <table class="vault-table">
          <colgroup>
            ${columns
              .map((column) => `<col style="width: ${getVaultColumnWidth(column.fieldName)}px;" />`)
              .join("")}
          </colgroup>
          <thead>
            <tr>
              ${columns
                .map(
                  (column) => `
                    <th class="${column.isPrimaryKey ? "pk-header" : ""}">
                      <div class="vault-header-cell">
                        <button
                          class="vault-header-button ${state.vaultSortField === column.fieldName ? "is-active" : ""}"
                          data-action="vault-sort"
                          data-field="${escapeAttribute(column.fieldName)}"
                          title="Sort by ${escapeAttribute(column.fieldName)}"
                        >
                          <span>${escapeHtml(column.fieldName)}</span>
                          <span class="vault-sort-indicator">${renderVaultSortIndicator(column.fieldName)}</span>
                        </button>
                        ${renderIconButton(
                          "vault-filter",
                          getVaultFilterValue(column.fieldName)
                            ? `Edit filter for ${column.fieldName}`
                            : `Filter ${column.fieldName}`,
                          "filter",
                          getVaultFilterValue(column.fieldName) ? "success" : "neutral",
                          { field: column.fieldName }
                        )}
                        <div
                          class="column-resizer"
                          data-field="${escapeAttribute(column.fieldName)}"
                          title="Drag to resize. Double click to auto-fit."
                        ></div>
                      </div>
                    </th>
                  `
                )
                .join("")}
            </tr>
          </thead>
          <tbody>
            ${
              rows.length
                ? rows
                    .map(({ row, sourceIndex }) => {
                      const dirty = !!state.dirtyVaultRows[sourceIndex];
                      return `
                        <tr class="${dirty ? "row-dirty" : ""}">
                          ${columns
                            .map((column) => {
                              const cellDirty = !!state.dirtyVaultRows[sourceIndex]?.[column.fieldName];
                              const cellKey = `${sourceIndex}:${column.fieldName}`;
                              const expanded = !!state.expandedVaultCells[cellKey];
                              return `
                                <td
                                  class="${column.isPrimaryKey ? "pk-cell" : ""} ${cellDirty ? "cell-dirty" : ""} ${expanded ? "cell-expanded" : ""}"
                                  data-cell-key="${escapeAttribute(cellKey)}"
                                  title="Double click to expand or collapse"
                                >
                                  <div class="cell-layout">
                                    <div class="cell-preview mono">${renderValuePreview(row[column.fieldName], !expanded)}</div>
                                    ${renderIconButton(
                                      "vault-edit-cell",
                                      `Edit ${column.fieldName}`,
                                      "edit",
                                      cellDirty ? "success" : "accent",
                                      {
                                        rowIndex: sourceIndex,
                                        field: column.fieldName,
                                      }
                                    )}
                                  </div>
                                </td>
                              `;
                            })
                            .join("")}
                        </tr>
                      `;
                    })
                    .join("")
                : `<tr><td colspan="${columns.length || 1}" class="muted">No rows matched the current client-side filters.</td></tr>`
            }
          </tbody>
        </table>
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
          ${renderDetail("SQL Query", definition.supportsSqlQuery ? "Supported" : "Unavailable")}
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

  function renderInlineError() {
    return state.error
      ? `
          <div class="inline-error">
            <strong>Error</strong>
            <span>${escapeHtml(state.error)}</span>
          </div>
        `
      : "";
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

  function orderedVaultColumns(definition, fields) {
    const defs = definition?.columns || [];
    const byField = new Map(defs.map((column) => [column.fieldName, column]));
    const present = fields.map((field) => byField.get(field) || { fieldName: field, isPrimaryKey: false });
    const primary = present.filter((column) => column.isPrimaryKey);
    const rest = present.filter((column) => !column.isPrimaryKey);
    return [...primary, ...rest];
  }

  function getCacheEntry(index) {
    return state.payload?.entries?.[index] || null;
  }

  function buildPanelRefreshState() {
    if (state.kind !== "vault") {
      return null;
    }

    return {
      selectedVaultTypeKey: state.selectedVaultTypeKey,
      skip: state.vaultPage?.skip || 0,
      take: getVaultTake(),
    };
  }

  function getVaultTake() {
    return state.vaultPage?.take || state.payload?.firstItems?.take || 50;
  }

  function beginLoading(label) {
    state.loadingLabel = label || "Loading...";
    state.isBootstrapping = !state.payload;
    state.isRefreshing = !!state.payload;
    render();
  }

  function closeModal() {
    state.modal = null;
    render();
  }

  function syncModalEditorPreview() {
    const code = app.querySelector(".json-view--editor code");
    if (!code || !state.modal) {
      return;
    }

    code.innerHTML = renderHighlightedJson(prettyJsonOrString(state.modal.editValue || ""));
  }

  function syncModalEditorScroll(textarea) {
    const pre = app.querySelector(".json-view--editor");
    if (!pre) {
      return;
    }

    pre.scrollTop = textarea.scrollTop;
    pre.scrollLeft = textarea.scrollLeft;
  }

  function openConfigModal(key) {
    const entry = (state.payload?.configs || []).find((item) => item.key === key);
    if (!entry || !entry.modifiable) {
      return;
    }

    const currentValue = Object.prototype.hasOwnProperty.call(state.dirtyConfig, key)
      ? state.dirtyConfig[key]
      : stringifyValue(entry.value);

    state.modal = {
      type: "config",
      title: "Edit Live Config",
      subtitle: key,
      prettyValue: prettyJsonOrString(entry.value),
      editValue: currentValue,
      saveAction: "stage-config-change",
      saveLabel: "Stage Config Change",
      saveTone: "success",
      key,
      error: "",
    };
    render();
  }

  function openCacheModal(index) {
    const entry = getCacheEntry(index);
    if (!entry) {
      return;
    }

    state.modal = {
      type: "cache",
      title: "Edit Cache Entry",
      subtitle: `${entry.key} (${entry.typeShortName || entry.type})`,
      prettyValue: JSON.stringify(entry.value, null, 2),
      editValue: JSON.stringify(entry.value, null, 2),
      saveAction: "save-cache-entry",
      saveLabel: "Save Cache Entry",
      saveTone: "accent",
      index,
      error: "",
    };
    render();
  }

  function openVaultCellModal(rowIndex, field) {
    const row = state.vaultPage?.items?.[rowIndex];
    if (!row || !field) {
      return;
    }

    const value = row[field];
    state.modal = {
      type: "vaultCell",
      title: "Edit Vault Cell",
      subtitle: `${state.selectedVaultTypeKey} :: ${field}`,
      prettyValue: prettyJsonOrString(value),
      editValue: editStringFromValue(value),
      saveAction: "save-vault-cell",
      saveLabel: "Stage Cell Change",
      saveTone: "success",
      rowIndex,
      field,
      error: "",
    };
    render();
  }

  function openVaultFilterModal(field) {
    if (!field) {
      return;
    }

    state.modal = {
      type: "vaultFilter",
      title: "Filter Vault Column",
      subtitle: `${state.selectedVaultTypeKey} :: ${field}`,
      prettyValue: getVaultFilterValue(field)
        ? JSON.stringify({ field, contains: getVaultFilterValue(field) }, null, 2)
        : JSON.stringify({ field, contains: "" }, null, 2),
      editValue: getVaultFilterValue(field),
      saveAction: "stage-vault-filter",
      saveLabel: "Apply Filter",
      saveTone: "success",
      field,
      error: "",
    };
    render();
  }

  function saveCacheModal() {
    if (!state.modal || state.modal.type !== "cache") {
      return;
    }

    let parsed;
    try {
      parsed = JSON.parse(state.modal.editValue);
    } catch (error) {
      state.modal.error = `Invalid JSON: ${error instanceof Error ? error.message : String(error)}`;
      render();
      return;
    }

    const entry = getCacheEntry(state.modal.index);
    if (!entry) {
      return;
    }

    closeModal();
    beginLoading("Updating cache entry...");
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

  function stageConfigModal() {
    if (!state.modal || state.modal.type !== "config") {
      return;
    }

    const key = state.modal.key;
    const entry = (state.payload?.configs || []).find((item) => item.key === key);
    if (!entry) {
      return;
    }

    const nextValue = state.modal.editValue ?? "";
    const originalValue = stringifyValue(entry.value);

    if (nextValue === originalValue) {
      delete state.dirtyConfig[key];
    } else {
      state.dirtyConfig[key] = nextValue;
    }

    closeModal();
    render();
  }

  function stageVaultFilterModal() {
    if (!state.modal || state.modal.type !== "vaultFilter") {
      return;
    }

    const field = state.modal.field;
    const nextValue = String(state.modal.editValue || "").trim();

    if (!nextValue) {
      delete state.vaultFilters[field];
    } else {
      state.vaultFilters[field] = nextValue;
    }

    closeModal();
    render();
  }

  function saveVaultCellModal() {
    if (!state.modal || state.modal.type !== "vaultCell") {
      return;
    }

    const rowIndex = state.modal.rowIndex;
    const field = state.modal.field;
    const original = state.vaultOriginalItems?.[rowIndex]?.[field];
    let parsed;

    try {
      parsed = parseEditedValue(state.modal.editValue, original);
    } catch (error) {
      state.modal.error = `Invalid value: ${error instanceof Error ? error.message : String(error)}`;
      render();
      return;
    }

    if (!state.vaultPage?.items?.[rowIndex]) {
      return;
    }

    state.vaultPage.items[rowIndex][field] = parsed;

    if (valuesEqual(parsed, original)) {
      if (state.dirtyVaultRows[rowIndex]) {
        delete state.dirtyVaultRows[rowIndex][field];
        if (Object.keys(state.dirtyVaultRows[rowIndex]).length === 0) {
          delete state.dirtyVaultRows[rowIndex];
        }
      }
    } else {
      state.dirtyVaultRows[rowIndex] = state.dirtyVaultRows[rowIndex] || {};
      state.dirtyVaultRows[rowIndex][field] = parsed;
    }

    closeModal();
    render();
  }

  function commitConfigChanges() {
    const entries = getDirtyConfigEntries();
    if (!entries.length) {
      return;
    }

    closeModal();
    beginLoading("Saving live config changes...");
    vscode.postMessage({
      type: "config:update-batch",
      entries,
    });
  }

  function discardConfigChanges() {
    state.dirtyConfig = {};
    closeModal();
    render();
  }

  function commitVaultChanges() {
    if (!state.selectedVaultTypeKey || !hasVaultChanges()) {
      return;
    }

    const columns = orderedVaultColumns(
      (state.payload?.definitions || []).find((item) => item.typeKey === state.selectedVaultTypeKey),
      state.vaultPage?.fields || []
    );
    const pkFields = columns.filter((column) => column.isPrimaryKey).map((column) => column.fieldName);
    const items = Object.entries(state.dirtyVaultRows).map(([rowIndex, changes]) => {
      const row = state.vaultPage.items[Number(rowIndex)];
      const payload = {};

      for (const field of pkFields) {
        payload[field] = row[field];
      }

      for (const [field, value] of Object.entries(changes)) {
        payload[field] = value;
      }

      return payload;
    });

    beginLoading("Committing vault changes...");
    vscode.postMessage({
      type: "vault:batchUpdate",
      typeKey: state.selectedVaultTypeKey,
      skip: state.vaultPage?.skip || 0,
      take: getVaultTake(),
      items,
    });
  }

  function discardVaultChanges() {
    if (state.vaultPage) {
      state.vaultPage.items = cloneValue(state.vaultOriginalItems);
    }
    state.dirtyVaultRows = {};
    closeModal();
    render();
  }

  function requestVaultPage(skip, take) {
    if (!state.selectedVaultTypeKey) {
      return;
    }

    state.vaultQueryResult = null;
    beginLoading("Loading vault items...");
    vscode.postMessage({
      type: "vault:loadItems",
      typeKey: state.selectedVaultTypeKey,
      skip,
      take,
    });
  }

  function executeVaultQuery() {
    if (!state.selectedVaultTypeKey || !state.vaultQuerySql.trim()) {
      return;
    }

    beginLoading("Executing SQL query...");
    vscode.postMessage({
      type: "vault:query",
      typeKey: state.selectedVaultTypeKey,
      sql: state.vaultQuerySql,
    });
  }

  function applyVaultPage(page) {
    state.vaultPage = cloneValue(page);
    state.vaultOriginalItems = cloneValue(page?.items || []);
    state.dirtyVaultRows = {};
  }

  function confirmAndSend(message, payload, label) {
    if (!window.confirm(message)) {
      return;
    }

    beginLoading(label);
    vscode.postMessage(payload);
  }

  function confirmVaultDiscard(message) {
    if (!hasVaultChanges()) {
      return true;
    }

    return window.confirm(message);
  }

  function getDirtyConfigEntries() {
    return Object.entries(state.dirtyConfig).map(([key, value]) => ({ key, value }));
  }

  function reconcileDirtyConfig(dirtyConfig, configs) {
    const next = {};
    const valuesByKey = new Map((configs || []).map((entry) => [entry.key, stringifyValue(entry.value)]));

    for (const [key, value] of Object.entries(dirtyConfig || {})) {
      if (!valuesByKey.has(key)) {
        continue;
      }

      if (valuesByKey.get(key) === value) {
        continue;
      }

      next[key] = value;
    }

    return next;
  }

  function hasVaultChanges() {
    return Object.keys(state.dirtyVaultRows).length > 0;
  }

  function getVaultDisplayRows(page, columns) {
    const filters = state.vaultFilters || {};
    const rows = (page.items || []).map((row, index) => ({ row, sourceIndex: index }));
    const filtered = rows.filter(({ row }) =>
      columns.every((column) => {
        const filterValue = getVaultFilterValue(column.fieldName);
        if (!filterValue) {
          return true;
        }

        return stringifyValue(row[column.fieldName]).toLowerCase().includes(filterValue.toLowerCase());
      })
    );

    if (!state.vaultSortField) {
      return filtered;
    }

    const direction = state.vaultSortDirection === "desc" ? -1 : 1;
    const field = state.vaultSortField;

    return filtered.slice().sort((left, right) => {
      const result = compareVaultValues(left.row[field], right.row[field]);
      if (result !== 0) {
        return result * direction;
      }

      return left.sourceIndex - right.sourceIndex;
    });
  }

  function toggleVaultSort(field) {
    if (!field) {
      return;
    }

    if (state.vaultSortField !== field) {
      state.vaultSortField = field;
      state.vaultSortDirection = "asc";
      render();
      return;
    }

    if (state.vaultSortDirection === "asc") {
      state.vaultSortDirection = "desc";
      render();
      return;
    }

    state.vaultSortField = "";
    state.vaultSortDirection = "asc";
    render();
  }

  function getVaultFilterValue(field) {
    return String(state.vaultFilters?.[field] || "");
  }

  function renderVaultSortIndicator(field) {
    if (state.vaultSortField !== field) {
      return "&#x2195;";
    }

    return state.vaultSortDirection === "desc" ? "&#x2193;" : "&#x2191;";
  }

  function compareVaultValues(left, right) {
    if (left == null && right == null) {
      return 0;
    }
    if (left == null) {
      return -1;
    }
    if (right == null) {
      return 1;
    }

    if (typeof left === "number" && typeof right === "number") {
      return left - right;
    }

    if (typeof left === "boolean" && typeof right === "boolean") {
      return Number(left) - Number(right);
    }

    const leftString = stringifyValue(left).toLowerCase();
    const rightString = stringifyValue(right).toLowerCase();

    if (leftString < rightString) {
      return -1;
    }
    if (leftString > rightString) {
      return 1;
    }

    return 0;
  }

  function toggleVaultCellExpansion(cellKey) {
    if (!cellKey) {
      return;
    }

    if (state.expandedVaultCells[cellKey]) {
      delete state.expandedVaultCells[cellKey];
    } else {
      state.expandedVaultCells[cellKey] = true;
    }

    render();
  }

  function startColumnResize(field, clientX) {
    if (!field) {
      return;
    }

    activeColumnResize = {
      field,
      startX: clientX,
      startWidth: getVaultColumnWidth(field),
    };

    document.body.classList.add("is-col-resizing");
  }

  function autoSizeVaultColumn(field) {
    if (!field) {
      return;
    }

    const nextWidth = Math.max(90, measureVaultColumnWidth(field));
    setVaultColumnWidth(field, nextWidth);
    render();
  }

  function getVaultColumnWidth(field) {
    const stored = state.vaultColumnWidths[getVaultColumnStorageKey(field)];
    return Number.isFinite(stored) ? stored : 160;
  }

  function setVaultColumnWidth(field, width) {
    state.vaultColumnWidths[getVaultColumnStorageKey(field)] = Math.max(90, Math.round(width));
  }

  function getVaultColumnStorageKey(field) {
    return `${state.selectedVaultTypeKey || "vault"}::${field}`;
  }

  function measureVaultColumnWidth(field) {
    const values = [field];

    for (const row of state.vaultPage?.items || []) {
      values.push(prettyInlineValue(row[field]));
    }

    const probe = document.createElement("span");
    probe.style.position = "fixed";
    probe.style.visibility = "hidden";
    probe.style.whiteSpace = "pre";
    probe.style.fontFamily = "var(--vscode-editor-font-family, Consolas, monospace)";
    probe.style.fontSize = "12px";
    document.body.appendChild(probe);

    let maxWidth = 0;
    for (const value of values) {
      probe.textContent = String(value ?? "");
      maxWidth = Math.max(maxWidth, Math.ceil(probe.getBoundingClientRect().width));
    }

    probe.remove();
    return maxWidth + 42;
  }

  function parseEditedValue(raw, original) {
    const trimmed = String(raw ?? "").trim();

    if (original === null || typeof original === "object") {
      if (!trimmed) {
        return null;
      }
      return JSON.parse(trimmed);
    }

    if (typeof original === "number") {
      const parsed = Number(trimmed);
      if (Number.isNaN(parsed)) {
        throw new Error("Expected numeric value.");
      }
      return parsed;
    }

    if (typeof original === "boolean") {
      if (trimmed === "true") {
        return true;
      }
      if (trimmed === "false") {
        return false;
      }
      throw new Error("Expected true or false.");
    }

    if (original === undefined) {
      if (!trimmed) {
        return "";
      }
      try {
        return JSON.parse(trimmed);
      } catch {
        return raw;
      }
    }

    return raw;
  }

  function renderIconButton(action, title, icon, tone, dataset = {}, disabled = false) {
    const attrs = Object.entries(dataset)
      .map(([key, value]) => `data-${camelToKebab(key)}="${escapeAttribute(String(value))}"`)
      .join(" ");
    return `
      <button
        class="icon-button icon-button--${escapeHtml(tone || "neutral")}"
        data-action="${escapeAttribute(action)}"
        title="${escapeAttribute(title)}"
        aria-label="${escapeAttribute(title)}"
        ${disabled ? "disabled" : ""}
        ${attrs}
      >
        ${iconMarkup(icon)}
      </button>
    `;
  }

  function iconMarkup(icon) {
    switch (icon) {
      case "refresh":
        return "&#x21BB;";
      case "edit":
        return "&#x270E;";
      case "delete":
        return '<svg viewBox="0 0 16 16" width="12" height="12" aria-hidden="true"><path d="M6 2h4l.6 1H13v1H3V3h2.4L6 2zm-1 3h1v7H5V5zm3 0h1v7H8V5zm3 0h1v7h-1V5zM4 13h8a1 1 0 0 0 1-1V4H3v8a1 1 0 0 0 1 1z" fill="currentColor"></path></svg>';
      case "copy":
        return '<svg viewBox="0 0 16 16" width="12" height="12" aria-hidden="true"><path d="M5 2h7a1 1 0 0 1 1 1v8h-1V3H5V2zM3 5h7a1 1 0 0 1 1 1v7a1 1 0 0 1-1 1H3a1 1 0 0 1-1-1V6a1 1 0 0 1 1-1zm0 1v7h7V6H3z" fill="currentColor"></path></svg>';
      case "prev":
        return "&#x2190;";
      case "next":
        return "&#x2192;";
      case "save":
        return "&#x2714;";
      case "execute":
        return "&#x25B6;";
      case "filter":
        return '<svg viewBox="0 0 16 16" width="12" height="12" aria-hidden="true"><path d="M2 3h12L9.5 8.2v4.1l-3-1.7V8.2L2 3z" fill="currentColor"></path></svg>';
      case "disconnect":
        return "&#x2715;";
      case "close":
      case "cancel":
        return "&#x2715;";
      default:
        return "&#x2022;";
    }
  }

  function renderValuePreview(value, truncate) {
    const asString = prettyInlineValue(value);
    const rendered = truncate ? truncateText(asString, 30) : asString;
    return escapeHtml(rendered);
  }

  function prettyInlineValue(value) {
    if (value == null) {
      return "null";
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

  function prettyJsonOrString(value) {
    if (value == null) {
      return "null";
    }
    if (typeof value === "string") {
      const trimmed = value.trim();
      if ((trimmed.startsWith("{") && trimmed.endsWith("}")) || (trimmed.startsWith("[") && trimmed.endsWith("]"))) {
        try {
          return JSON.stringify(JSON.parse(trimmed), null, 2);
        } catch {
          return value;
        }
      }
      return value;
    }

    try {
      return JSON.stringify(value, null, 2);
    } catch {
      return String(value);
    }
  }

  function editStringFromValue(value) {
    if (value == null) {
      return "null";
    }
    if (typeof value === "string") {
      return value;
    }
    if (typeof value === "object") {
      return JSON.stringify(value, null, 2);
    }
    return String(value);
  }

  function truncateText(value, limit) {
    if (value.length <= limit) {
      return value;
    }
    return `${value.slice(0, limit)}...`;
  }

  function renderHighlightedJson(raw) {
    const json = escapeHtml(raw);
    return json.replace(
      /(&quot;.*?&quot;)(\s*:)?|(\btrue\b|\bfalse\b|\bnull\b)|(-?\d+(?:\.\d+)?)/g,
      (match, stringToken, keySuffix, keywordToken, numberToken) => {
        if (stringToken) {
          const cls = keySuffix ? "json-key" : "json-string";
          return `<span class="${cls}">${stringToken}</span>${keySuffix || ""}`;
        }
        if (keywordToken) {
          return `<span class="json-literal">${keywordToken}</span>`;
        }
        if (numberToken) {
          return `<span class="json-number">${numberToken}</span>`;
        }
        return match;
      }
    );
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

  function valuesEqual(left, right) {
    return JSON.stringify(left) === JSON.stringify(right);
  }

  function cloneValue(value) {
    return value == null ? value : JSON.parse(JSON.stringify(value));
  }

  function camelToKebab(value) {
    return String(value).replace(/[A-Z]/g, (match) => `-${match.toLowerCase()}`);
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
