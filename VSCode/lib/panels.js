const vscode = require("vscode");
const { DashboardClient } = require("./dashboardClient");
const { toErrorMessage } = require("./connectionManager");

const GENERIC_PANEL_TITLES = {
  summary: "Altruist Summary",
  config: "Altruist Config",
  sessions: "Altruist Sessions",
  cache: "Altruist Cache",
  vault: "Altruist Vault",
  network: "Altruist Network",
  performance: "Altruist Performance",
  lab: "Altruist API Lab",
};
const LAB_STATE_KEY = "altruist.dashboard.labState.v1";

class PanelRegistry {
  constructor(context, connectionManager, outputChannel) {
    this.context = context;
    this.connectionManager = connectionManager;
    this.outputChannel = outputChannel;
    this.genericPanels = new Map();
    this.worldPanel = undefined;
    this.currentPanel = undefined;
  }

  async openGeneric(kind) {
    let panel = this.genericPanels.get(kind);
    if (!panel) {
      panel = new GenericDashboardPanel(
        this.context,
        this.connectionManager,
        this.outputChannel,
        kind
      );
      this.genericPanels.set(kind, panel);
      panel.onDidDispose(() => {
        this.genericPanels.delete(kind);
        if (this.currentPanel === panel) {
          this.currentPanel = undefined;
        }
      });
    }

    await panel.show();
    this.currentPanel = panel;
  }

  async openWorld() {
    if (!this.worldPanel) {
      this.worldPanel = new WorldDashboardPanel(
        this.context,
        this.connectionManager,
        this.outputChannel
      );
      this.worldPanel.onDidDispose(() => {
        if (this.currentPanel === this.worldPanel) {
          this.currentPanel = undefined;
        }
        this.worldPanel = undefined;
      });
    }

    await this.worldPanel.show();
    this.currentPanel = this.worldPanel;
  }

  async refreshCurrentPanel() {
    if (!this.currentPanel) {
      vscode.window.showInformationMessage("No Altruist panel is currently open.");
      return;
    }

    await this.currentPanel.refresh();
  }
}

class GenericDashboardPanel {
  constructor(context, connectionManager, outputChannel, kind) {
    this.context = context;
    this.connectionManager = connectionManager;
    this.outputChannel = outputChannel;
    this.kind = kind;
    this._onDidDispose = new vscode.EventEmitter();

    this.panel = vscode.window.createWebviewPanel(
      `altruist.${kind}`,
      GENERIC_PANEL_TITLES[kind] || "Altruist",
      vscode.ViewColumn.Active,
      {
        enableScripts: true,
        retainContextWhenHidden: false,
        localResourceRoots: [vscode.Uri.joinPath(context.extensionUri, "media")],
      }
    );

    this.panel.webview.html = buildGenericPanelHtml(this.panel.webview, context, kind);
    this.panel.webview.onDidReceiveMessage(
      (message) => this.handleMessage(message),
      undefined,
      this.context.subscriptions
    );
    this.panel.onDidDispose(() => this._onDidDispose.fire(), undefined, this.context.subscriptions);
  }

  onDidDispose(listener) {
    return this._onDidDispose.event(listener);
  }

  async show() {
    this.panel.reveal(vscode.ViewColumn.Active);
    await this.refresh();
  }

  postLoading(label = "Loading dashboard data...") {
    this.panel.webview.postMessage({
      type: "panel:loading",
      kind: this.kind,
      label,
    });
  }

  async refresh(viewState) {
    this.postLoading("Loading dashboard data...");

    try {
      await this.connectionManager.ensureConnected();
      const state = this.connectionManager.getState();
      const client = new DashboardClient(state.baseUrl);
      const payload = await loadGenericPanelPayload(client, this.kind, viewState);
      if (this.kind === "lab" && payload && typeof payload === "object") {
        payload.labState = this.context.workspaceState.get(LAB_STATE_KEY, {
          version: 1,
          default: {},
          groups: [],
        });
      }

      this.panel.webview.postMessage({
        type: "panel:data",
        kind: this.kind,
        baseUrl: state.baseUrl,
        payload,
      });
    } catch (error) {
      this.connectionManager.setLastError(error);
      this.panel.webview.postMessage({
        type: "panel:error",
        kind: this.kind,
        error: toErrorMessage(error),
      });
    }
  }

  async handleMessage(message) {
    const state = this.connectionManager.getState();
    const client = new DashboardClient(state.baseUrl);

    try {
      switch (message?.type) {
        case "panel:ready":
        case "panel:refresh":
          await this.refresh(message?.state);
          return;
        case "config:update-batch":
          this.postLoading("Saving live config changes...");
          await client.updateConfigBatch(message.entries || []);
          await this.refresh();
          return;
        case "sessions:closeConnection":
          this.postLoading("Disconnecting session...");
          await client.closeSession(message.connectionId);
          await this.refresh();
          return;
        case "sessions:deleteRoom":
          this.postLoading("Deleting room...");
          await client.deleteRoom(message.roomId);
          await this.refresh();
          return;
        case "sessions:removeRoomConnection":
          this.postLoading("Removing connection from room...");
          await client.removeConnectionFromRoom(message.roomId, message.connectionId);
          await this.refresh();
          return;
        case "cache:update":
          this.postLoading("Updating cache entry...");
          await client.updateCacheEntry(message.entry);
          await this.refresh();
          return;
        case "cache:delete":
          this.postLoading("Deleting cache entry...");
          await client.deleteCacheEntry(message.entry);
          await this.refresh();
          return;
        case "vault:loadItems": {
          this.postLoading("Loading vault items...");
          const items = await client.getVaultItems(
            message.typeKey,
            message.skip ?? 0,
            message.take ?? 50
          );
          this.panel.webview.postMessage({
            type: "vault:items",
            typeKey: message.typeKey,
            payload: items,
          });
          return;
        }
        case "vault:batchUpdate": {
          this.postLoading("Committing vault changes...");
          await client.batchUpdateVault(message.typeKey, message.items ?? []);
          const items = await client.getVaultItems(
            message.typeKey,
            message.skip ?? 0,
            message.take ?? 50
          );
          this.panel.webview.postMessage({
            type: "vault:items",
            typeKey: message.typeKey,
            payload: items,
          });
          return;
        }
        case "vault:query": {
          this.postLoading("Executing SQL query...");
          const result = await client.queryVault(message.typeKey, message.sql || "");
          this.panel.webview.postMessage({
            type: "vault:queryResult",
            typeKey: message.typeKey,
            payload: result,
          });
          return;
        }
        case "lab:invoke": {
          this.postLoading("Invoking endpoint...");
          const result = await client.invokeLabAction(message.request || {});
          this.panel.webview.postMessage({
            type: "lab:result",
            actionId: message.actionId,
            payload: result,
          });
          return;
        }
        case "lab:invoke-sequence": {
          this.postLoading(message.label || "Running lab group...");
          for (const item of message.items || []) {
            try {
              const result = await client.invokeLabAction(item.request || {});
              this.panel.webview.postMessage({
                type: "lab:result",
                actionId: item.actionId,
                payload: result,
              });
            } catch (error) {
              this.panel.webview.postMessage({
                type: "lab:result",
                actionId: item.actionId,
                payload: {
                  success: false,
                  message: toErrorMessage(error),
                },
              });
            }
          }
          return;
        }
        case "lab:save-state":
          await this.context.workspaceState.update(
            LAB_STATE_KEY,
            normalizeLabState(message.state)
          );
          return;
        default:
          return;
      }
    } catch (error) {
      this.connectionManager.setLastError(error);
      this.panel.webview.postMessage({
        type: "panel:error",
        kind: this.kind,
        error: toErrorMessage(error),
      });
    }
  }
}

function normalizeLabState(value) {
  const groups = Array.isArray(value?.groups)
    ? value.groups.map((group) => ({
        id: String(group?.id || "").trim(),
        name: String(group?.name || "").trim(),
        items: Array.isArray(group?.items)
          ? group.items.map((item) => ({
              instanceId: String(item?.instanceId || "").trim(),
              actionId: String(item?.actionId || "").trim(),
              bodyJson: String(item?.bodyJson ?? ""),
              clientId: String(item?.clientId ?? ""),
            })).filter((item) => item.instanceId && item.actionId)
          : [],
      })).filter((group) => group.id && group.name)
    : [];

  return {
    version: 1,
    default: value?.default && typeof value.default === "object" ? value.default : {},
    groups,
  };
}

class WorldDashboardPanel {
  constructor(context, connectionManager, outputChannel) {
    this.context = context;
    this.connectionManager = connectionManager;
    this.outputChannel = outputChannel;
    this.selectedWorldIndex = vscode.workspace
      .getConfiguration("altruist.dashboard")
      .get("defaultWorldIndex", 0);
    this.webviewReady = false;
    this.pendingRefresh = false;
    this.streamRequestId = 0;
    this._onDidDispose = new vscode.EventEmitter();

    this.panel = vscode.window.createWebviewPanel(
      "altruist.worlds",
      "Altruist Worlds",
      vscode.ViewColumn.Active,
      {
        enableScripts: true,
        retainContextWhenHidden: false,
        localResourceRoots: [
          vscode.Uri.joinPath(context.extensionUri, "media"),
          vscode.Uri.joinPath(context.extensionUri, "media", "vendor"),
        ],
      }
    );

    this.panel.webview.html = buildWorldPanelHtml(this.panel.webview, context);
    this.panel.webview.onDidReceiveMessage(
      (message) => this.handleMessage(message),
      undefined,
      this.context.subscriptions
    );
    this.panel.onDidDispose(() => {
      this.streamRequestId += 1;
      this._onDidDispose.fire();
    }, undefined, this.context.subscriptions);
  }

  onDidDispose(listener) {
    return this._onDidDispose.event(listener);
  }

  async show() {
    this.panel.reveal(vscode.ViewColumn.Active);
    if (this.webviewReady) {
      await this.refresh();
      return;
    }

    this.pendingRefresh = true;
    this.outputChannel.appendLine(
      "[WorldDashboard] Waiting for webview readiness before sending bootstrap."
    );
  }

  async refresh() {
    if (!this.webviewReady) {
      this.pendingRefresh = true;
      this.outputChannel.appendLine(
        "[WorldDashboard] Refresh requested before webview ready; queued."
      );
      return;
    }

    try {
      await this.connectionManager.ensureConnected();
      const connection = this.connectionManager.getState();
      const client = new DashboardClient(connection.baseUrl);
      const summary = connection.summary ?? (await client.getSummary());
      const worlds = await client.getWorlds();

      const preferredWorld =
        worlds.find((world) => world.index === this.selectedWorldIndex) ??
        worlds[0] ??
        null;
      const dashboardWebsocketUrl = deriveDashboardWebsocketUrl(connection.baseUrl);

      this.outputChannel.appendLine(
        `[WorldDashboard] Posting bootstrap baseUrl=${connection.baseUrl} ws=${dashboardWebsocketUrl || "-"} worlds=${worlds.length} selected=${preferredWorld?.index ?? "none"}`
      );

      this.panel.webview.postMessage({
        type: "world:bootstrap",
        payload: {
          baseUrl: connection.baseUrl,
          websocketUrl: dashboardWebsocketUrl,
          environmentMode:
            readConfigValue(summary.configs, "altruist:environment:mode") ??
            connection.environmentMode ??
            "3D",
          worlds,
          selectedWorldIndex: preferredWorld?.index ?? null,
          summary,
          snapshot: preferredWorld
            ? createEmptyWorldSnapshot(preferredWorld)
            : null,
        },
      });
    } catch (error) {
      this.connectionManager.setLastError(error);
      this.outputChannel.appendLine(
        `[WorldDashboard] Refresh failed: ${toErrorMessage(error)}`
      );
      this.panel.webview.postMessage({
        type: "world:error",
        error: toErrorMessage(error),
      });
    }
  }

  async handleMessage(message) {
    try {
      switch (message?.type) {
        case "world:ready":
          this.webviewReady = true;
          this.outputChannel.appendLine("[WorldDashboard] Webview reported ready.");
          this.pendingRefresh = false;
          await this.refresh();
          return;
        case "world:refresh":
          await this.refresh();
          return;
        case "world:select": {
          this.selectedWorldIndex = Number(message.worldIndex);
          const connection = this.connectionManager.getState();
          const client = new DashboardClient(connection.baseUrl);
          const worlds = await client.getWorlds();
          const world = worlds.find((item) => item.index === this.selectedWorldIndex) ?? null;
          this.panel.webview.postMessage({
            type: "world:snapshot",
            payload: {
              selectedWorldIndex: this.selectedWorldIndex,
              snapshot: world ? createEmptyWorldSnapshot(world) : null,
            },
          });
          return;
        }
        case "world:load-stream":
          await this.loadWorldStream(Number(message.worldIndex), Number(message.requestId));
          return;
        default:
          return;
      }
    } catch (error) {
      this.connectionManager.setLastError(error);
      this.panel.webview.postMessage({
        type: "world:error",
        error: toErrorMessage(error),
      });
    }
  }

  async loadWorldStream(worldIndex, requestId) {
    if (!Number.isFinite(worldIndex)) {
      return;
    }

    const streamRequestId = ++this.streamRequestId;
    const isCurrent = () => streamRequestId === this.streamRequestId;

    try {
      await this.connectionManager.ensureConnected();
      const connection = this.connectionManager.getState();
      const client = new DashboardClient(connection.baseUrl);

      this.outputChannel.appendLine(
        `[WorldDashboard] Streaming world objects world=${worldIndex} request=${requestId}.`
      );

      let partitions = 0;
      await client.streamWorldObjects(worldIndex, async (partition) => {
        if (!isCurrent()) {
          return false;
        }

        partitions += 1;
        await this.panel.webview.postMessage({
          type: "world:stream-partition",
          worldIndex,
          requestId,
          partition,
        });
        return true;
      });

      if (!isCurrent()) {
        return;
      }

      const gizmos = await client.getWorldGizmos(worldIndex);
      if (!isCurrent()) {
        return;
      }

      this.outputChannel.appendLine(
        `[WorldDashboard] Streamed ${partitions} partitions and ${Array.isArray(gizmos) ? gizmos.length : 0} gizmos for world=${worldIndex}.`
      );
      this.panel.webview.postMessage({
        type: "world:stream-complete",
        worldIndex,
        requestId,
        gizmos: Array.isArray(gizmos) ? gizmos : [],
      });
    } catch (error) {
      if (!isCurrent()) {
        return;
      }

      const message = toErrorMessage(error);
      this.connectionManager.setLastError(error);
      this.outputChannel.appendLine(
        `[WorldDashboard] Stream failed world=${worldIndex}: ${message}`
      );
      this.panel.webview.postMessage({
        type: "world:stream-error",
        worldIndex,
        requestId,
        error: message,
      });
    }
  }
}

function deriveDashboardWebsocketUrl(baseUrl) {
  const normalized = String(baseUrl || "").trim().replace(/\/+$/, "");
  if (!normalized) {
    return "";
  }

  if (/^https:\/\//i.test(normalized)) {
    return normalized.replace(/^https:\/\//i, "wss://") + "/ws/dashboard";
  }

  return normalized.replace(/^http:\/\//i, "ws://") + "/ws/dashboard";
}

async function loadGenericPanelPayload(client, kind, viewState) {
  switch (kind) {
    case "summary":
      return client.getSummary();
    case "config": {
      const summary = await client.getSummary();
      return { configs: summary.configs };
    }
    case "sessions": {
      const [rooms, connections] = await Promise.all([
        client.getSessions(),
        client.getConnections(),
      ]);
      return { rooms, connections };
    }
    case "cache": {
      const [info, entries] = await Promise.all([
        client.getCacheInfo(),
        client.getCacheEntries(),
      ]);
      return { info, entries };
    }
    case "vault": {
      const definitions = await client.getVaults();
      const selectedTypeKey = viewState?.selectedVaultTypeKey || definitions[0]?.typeKey;
      const skip = Number.isFinite(viewState?.skip) ? viewState.skip : 0;
      const take = Number.isFinite(viewState?.take) ? viewState.take : 50;
      const firstItems = selectedTypeKey
        ? await client.getVaultItems(selectedTypeKey, skip, take)
        : null;
      return { definitions, firstItems };
    }
    case "network":
      return client.getNetworkEvents({
        take: 500,
        kind: viewState?.networkKind || "",
        direction: viewState?.networkDirection || "",
        query: viewState?.networkFilter || "",
      });
    case "performance":
      return client.getPerformance();
    case "lab":
      return client.getLabActions();
    default:
      return {};
  }
}

function buildGenericPanelHtml(webview, context, kind) {
  const scriptUri = webview.asWebviewUri(
    vscode.Uri.joinPath(context.extensionUri, "media", "dashboardPanel.js")
  );
  const styleUri = webview.asWebviewUri(
    vscode.Uri.joinPath(context.extensionUri, "media", "dashboard.css")
  );
  const codiconsUri = webview.asWebviewUri(
    vscode.Uri.joinPath(context.extensionUri, "media", "vendor", "codicons", "codicon.css")
  );
  const codiconsFontUri = webview.asWebviewUri(
    vscode.Uri.joinPath(context.extensionUri, "media", "vendor", "codicons", "codicon.ttf")
  );
  const nonce = createNonce();

  return `<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource} data: https:; font-src ${webview.cspSource}; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}' ${webview.cspSource};" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <link rel="stylesheet" href="${codiconsUri}" />
    <style nonce="${nonce}">
      @font-face {
        font-family: "codicon";
        font-display: block;
        src: url("${codiconsFontUri}") format("truetype");
      }
    </style>
    <link rel="stylesheet" href="${styleUri}" />
    <title>Altruist</title>
  </head>
  <body data-kind="${kind}">
    <div id="app" class="panel-root">
      <div class="loading">Loading ${escapeHtml(GENERIC_PANEL_TITLES[kind] || "Altruist")}...</div>
    </div>
    <script nonce="${nonce}" src="${scriptUri}"></script>
  </body>
</html>`;
}

function buildWorldPanelHtml(webview, context) {
  const scriptUri = webview.asWebviewUri(
    vscode.Uri.joinPath(context.extensionUri, "media", "worldPanel.js")
  );
  const styleUri = webview.asWebviewUri(
    vscode.Uri.joinPath(context.extensionUri, "media", "world.css")
  );
  const codiconsUri = webview.asWebviewUri(
    vscode.Uri.joinPath(context.extensionUri, "media", "vendor", "codicons", "codicon.css")
  );
  const codiconsFontUri = webview.asWebviewUri(
    vscode.Uri.joinPath(context.extensionUri, "media", "vendor", "codicons", "codicon.ttf")
  );
  const nonce = createNonce();

  return `<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource} data: https:; font-src ${webview.cspSource}; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}' ${webview.cspSource}; connect-src https: http: ws: wss:; worker-src blob:;" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <link rel="stylesheet" href="${codiconsUri}" />
    <style nonce="${nonce}">
      @font-face {
        font-family: "codicon";
        font-display: block;
        src: url("${codiconsFontUri}") format("truetype");
      }
    </style>
    <link rel="stylesheet" href="${styleUri}" />
    <title>Altruist Worlds</title>
  </head>
  <body>
    <div id="world-root" class="world-root">
      <div class="loading">Loading Altruist world view...</div>
    </div>
    <script type="module" nonce="${nonce}" src="${scriptUri}"></script>
  </body>
</html>`;
}

function createNonce() {
  const chars =
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
  let result = "";
  for (let i = 0; i < 16; i++) {
    result += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return result;
}

function escapeHtml(value) {
  return String(value)
    .replaceAll("&", "&amp;")
    .replaceAll("<", "&lt;")
    .replaceAll(">", "&gt;");
}

function readConfigValue(configs, key) {
  return (configs || []).find((item) => item.key === key)?.value;
}

function createEmptyWorldSnapshot(world) {
  return {
    worldIndex: world.index,
    worldName: world.name || `World ${world.index}`,
    generatedAtUtc: new Date().toISOString(),
    renderOptions: {
      renderScale: 1,
      terrainSampleStride: 1,
    },
    partitions: [],
    gizmos: [],
  };
}

module.exports = {
  PanelRegistry,
};
