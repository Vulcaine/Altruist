const vscode = require("vscode");
const { DashboardClient } = require("./dashboardClient");
const { toErrorMessage } = require("./connectionManager");

const GENERIC_PANEL_TITLES = {
  summary: "Altruist Summary",
  config: "Altruist Config",
  sessions: "Altruist Sessions",
  cache: "Altruist Cache",
  vault: "Altruist Vault",
};

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
        retainContextWhenHidden: true,
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

  async refresh() {
    try {
      await this.connectionManager.ensureConnected();
      const state = this.connectionManager.getState();
      const client = new DashboardClient(state.baseUrl);
      const payload = await loadGenericPanelPayload(client, this.kind);

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
          await this.refresh();
          return;
        case "config:update":
          await client.updateConfig(message.key, message.value);
          await this.refresh();
          return;
        case "sessions:closeConnection":
          await client.closeSession(message.connectionId);
          await this.refresh();
          return;
        case "sessions:deleteRoom":
          await client.deleteRoom(message.roomId);
          await this.refresh();
          return;
        case "sessions:removeRoomConnection":
          await client.removeConnectionFromRoom(message.roomId, message.connectionId);
          await this.refresh();
          return;
        case "cache:update":
          await client.updateCacheEntry(message.entry);
          await this.refresh();
          return;
        case "cache:delete":
          await client.deleteCacheEntry(message.entry);
          await this.refresh();
          return;
        case "vault:loadItems": {
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
        case "vault:batchUpdate":
          await client.batchUpdateVault(message.typeKey, message.items ?? []);
          await this.refresh();
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

class WorldDashboardPanel {
  constructor(context, connectionManager, outputChannel) {
    this.context = context;
    this.connectionManager = connectionManager;
    this.outputChannel = outputChannel;
    this.selectedWorldIndex = vscode.workspace
      .getConfiguration("altruist.dashboard")
      .get("defaultWorldIndex", 0);
    this._onDidDispose = new vscode.EventEmitter();

    this.panel = vscode.window.createWebviewPanel(
      "altruist.worlds",
      "Altruist Worlds",
      vscode.ViewColumn.Active,
      {
        enableScripts: true,
        retainContextWhenHidden: true,
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
    this.panel.onDidDispose(() => this._onDidDispose.fire(), undefined, this.context.subscriptions);
  }

  onDidDispose(listener) {
    return this._onDidDispose.event(listener);
  }

  async show() {
    this.panel.reveal(vscode.ViewColumn.Active);
    await this.refresh();
  }

  async refresh() {
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

      const snapshot = preferredWorld
        ? await client.getWorldObjectsSnapshot(preferredWorld.index)
        : null;

      this.panel.webview.postMessage({
        type: "world:bootstrap",
        payload: {
          baseUrl: connection.baseUrl,
          websocketUrl: connection.websocketUrl,
          environmentMode:
            readConfigValue(summary.configs, "altruist:environment:mode") ??
            connection.environmentMode,
          worlds,
          selectedWorldIndex: preferredWorld?.index ?? null,
          summary,
          snapshot,
        },
      });
    } catch (error) {
      this.connectionManager.setLastError(error);
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
        case "world:refresh":
          await this.refresh();
          return;
        case "world:select": {
          this.selectedWorldIndex = Number(message.worldIndex);
          const connection = this.connectionManager.getState();
          const client = new DashboardClient(connection.baseUrl);
          const snapshot = await client.getWorldObjectsSnapshot(this.selectedWorldIndex);
          this.panel.webview.postMessage({
            type: "world:snapshot",
            payload: {
              selectedWorldIndex: this.selectedWorldIndex,
              snapshot,
            },
          });
          return;
        }
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
}

async function loadGenericPanelPayload(client, kind) {
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
      const first = definitions[0];
      const firstItems = first
        ? await client.getVaultItems(first.typeKey, 0, 50)
        : null;
      return { definitions, firstItems };
    }
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
  const nonce = createNonce();

  return `<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource} data: https:; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}' ${webview.cspSource};" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
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
  const bootstrapUri = webview.asWebviewUri(
    vscode.Uri.joinPath(context.extensionUri, "media", "worldPanel.js")
  );
  const styleUri = webview.asWebviewUri(
    vscode.Uri.joinPath(context.extensionUri, "media", "world.css")
  );
  const nonce = createNonce();

  return `<!DOCTYPE html>
<html lang="en">
  <head>
    <meta charset="UTF-8" />
    <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src ${webview.cspSource} data: https:; style-src ${webview.cspSource} 'unsafe-inline'; script-src 'nonce-${nonce}' ${webview.cspSource}; connect-src https: http: ws: wss:; worker-src blob:;" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <link rel="stylesheet" href="${styleUri}" />
    <title>Altruist Worlds</title>
  </head>
  <body>
    <div id="world-root" class="world-root">
      <div class="loading">Loading Altruist world view...</div>
    </div>
    <script nonce="${nonce}" type="module" src="${bootstrapUri}"></script>
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

module.exports = {
  PanelRegistry,
};
