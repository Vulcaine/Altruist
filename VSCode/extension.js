const vscode = require("vscode");
const { ConnectionManager, toErrorMessage } = require("./lib/connectionManager");
const { PanelRegistry } = require("./lib/panels");

function activate(context) {
  const output = vscode.window.createOutputChannel("Altruist Dashboard");
  const connectionManager = new ConnectionManager(output);
  const panelRegistry = new PanelRegistry(context, connectionManager, output);

  const connectionProvider = new ConnectionTreeProvider(connectionManager);
  const explorerProvider = new ExplorerTreeProvider();

  context.subscriptions.push(
    output,
    vscode.window.registerTreeDataProvider("altruistConnection", connectionProvider),
    vscode.window.registerTreeDataProvider("altruistExplorer", explorerProvider),
    vscode.commands.registerCommand("altruist.connectDashboard", async () => {
      try {
        await connectionManager.connect(true);
      } catch (error) {
        connectionManager.setLastError(error);
        vscode.window.showErrorMessage(toErrorMessage(error));
      }
    }),
    vscode.commands.registerCommand("altruist.disconnectDashboard", () => {
      connectionManager.disconnect(true);
    }),
    vscode.commands.registerCommand("altruist.refreshConnection", async () => {
      try {
        await connectionManager.refresh();
      } catch (error) {
        connectionManager.setLastError(error);
        vscode.window.showErrorMessage(toErrorMessage(error));
      }
    }),
    vscode.commands.registerCommand("altruist.openSummary", () => openPanel(panelRegistry, "summary")),
    vscode.commands.registerCommand("altruist.openWorlds", () => panelRegistry.openWorld()),
    vscode.commands.registerCommand("altruist.openConfig", () => openPanel(panelRegistry, "config")),
    vscode.commands.registerCommand("altruist.openSessions", () => openPanel(panelRegistry, "sessions")),
    vscode.commands.registerCommand("altruist.openCache", () => openPanel(panelRegistry, "cache")),
    vscode.commands.registerCommand("altruist.openVault", () => openPanel(panelRegistry, "vault")),
    vscode.commands.registerCommand("altruist.openNetwork", () => openPanel(panelRegistry, "network")),
    vscode.commands.registerCommand("altruist.openPerformance", () => openPanel(panelRegistry, "performance")),
    vscode.commands.registerCommand("altruist.openLab", () => openPanel(panelRegistry, "lab")),
    vscode.commands.registerCommand("altruist.refreshCurrentPanel", () => panelRegistry.refreshCurrentPanel()),
    vscode.commands.registerCommand("altruist.openSettings", () => {
      vscode.commands.executeCommand(
        "workbench.action.openSettings",
        "@ext:vulcaine.altruist-dashboard-vscode altruist.dashboard"
      );
    }),
    connectionManager.onDidChange(() => {
      connectionProvider.refresh();
      explorerProvider.refresh();
    })
  );

  connectionManager.autoConnectIfEnabled().catch((error) => {
    output.appendLine(`[activate] ${toErrorMessage(error)}`);
  });
}

function deactivate() {}

class ConnectionTreeProvider {
  constructor(connectionManager) {
    this.connectionManager = connectionManager;
    this._emitter = new vscode.EventEmitter();
    this.statusSection = new ConnectionSectionItem("Connection Status", "status");
    this.actionsSection = new ConnectionSectionItem("Actions", "actions");
  }

  get onDidChangeTreeData() {
    return this._emitter.event;
  }

  refresh() {
    this._emitter.fire();
  }

  getTreeItem(item) {
    return item;
  }

  getChildren(element) {
    const state = this.connectionManager.getState();
    if (!element) {
      return [this.statusSection, this.actionsSection];
    }

    if (element.sectionKind === "status") {
      const items = [];
      const statusText = state.connected
        ? "Connected"
        : state.lastError
          ? "Error"
          : "Disconnected";
      const statusIcon = state.connected
        ? "pass-filled"
        : state.lastError
          ? "warning"
          : "circle-large-outline";

      items.push(
        makeInfoItem("Status", statusText, {
          icon: statusIcon,
          tooltip: state.connected
            ? "The extension is connected to the Altruist dashboard."
            : state.lastError
              ? state.lastError
              : "The extension is not currently connected to the Altruist dashboard.",
        })
      );

      if (state.baseUrl) {
        items.push(
          makeInfoItem("Server", state.baseUrl, {
            icon: "link",
            tooltip: state.baseUrl,
          })
        );
      }

      if (state.websocketUrl) {
        items.push(
          makeInfoItem("World Stream", state.websocketUrl, {
            icon: "radio-tower",
            tooltip: state.websocketUrl,
          })
        );
      }

      if (state.configPath) {
        items.push(
          makeInfoItem("Config Source", state.configPath, {
            icon: "file-code",
            tooltip: state.configPath,
          })
        );
      }

      if (state.lastError) {
        items.push(
          makeInfoItem("Last Error", state.lastError, {
            icon: "error",
            tooltip: state.lastError,
          })
        );
      }

      return items;
    }

    if (element.sectionKind === "actions") {
      return [
        makeCommandItem(
          state.connected ? "Reconnect Dashboard" : "Connect to Dashboard",
          "altruist.connectDashboard",
          {
            description: state.connected ? "Probe again" : "Start connection",
            icon: "plug",
          }
        ),
        makeCommandItem("Disconnect Dashboard", "altruist.disconnectDashboard", {
          description: "Stop connection",
          icon: "debug-disconnect",
        }),
        makeCommandItem("Refresh Connection", "altruist.refreshConnection", {
          description: "Reload status",
          icon: "refresh",
        }),
        makeCommandItem("Open Extension Settings", "altruist.openSettings", {
          description: "Base URL and defaults",
          icon: "gear",
        }),
      ];
    }

    return [];
  }
}

class ExplorerTreeProvider {
  constructor() {
    this._emitter = new vscode.EventEmitter();
  }

  get onDidChangeTreeData() {
    return this._emitter.event;
  }

  refresh() {
    this._emitter.fire();
  }

  getTreeItem(item) {
    return item;
  }

  getChildren() {
    return [
      makeCommandItem("Summary", "altruist.openSummary", {
        description: "Engine and services",
        icon: "dashboard",
      }),
      makeCommandItem("Worlds", "altruist.openWorlds", {
        description: "3D world inspector",
        icon: "globe",
      }),
      makeCommandItem("Config", "altruist.openConfig", {
        description: "Live config values",
        icon: "settings-gear",
      }),
      makeCommandItem("Sessions", "altruist.openSessions", {
        description: "Rooms and connections",
        icon: "vm-connect",
      }),
      makeCommandItem("Cache", "altruist.openCache", {
        description: "Runtime cache entries",
        icon: "database",
      }),
      makeCommandItem("Vault", "altruist.openVault", {
        description: "Persistent storage",
        icon: "archive",
      }),
      makeCommandItem("Network", "altruist.openNetwork", {
        description: "Requests and packets",
        icon: "pulse",
      }),
      makeCommandItem("Performance", "altruist.openPerformance", {
        description: "Timings and slow paths",
        icon: "graph-line",
      }),
      makeCommandItem("API Lab", "altruist.openLab", {
        description: "Invoke endpoints and gates",
        icon: "beaker",
      }),
    ];
  }
}

function makeInfoItem(label) {
  const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
  item.contextValue = "info";
  return item;
}

class ConnectionSectionItem extends vscode.TreeItem {
  constructor(label, sectionKind) {
    super(label, vscode.TreeItemCollapsibleState.Expanded);
    this.sectionKind = sectionKind;
    this.contextValue = "sectionHeader";
    this.iconPath = new vscode.ThemeIcon(sectionKind === "actions" ? "play" : "pulse");
  }
}

function makeInfoItem(label, description, options = {}) {
  const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
  item.description = description;
  item.contextValue = "info";
  item.tooltip = options.tooltip || `${label}: ${description}`;
  if (options.icon) {
    item.iconPath = new vscode.ThemeIcon(options.icon);
  }
  return item;
}

function makeCommandItem(label, command, options = {}) {
  const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
  item.command = { command, title: label };
  item.contextValue = "section";
  if (options.description) {
    item.description = options.description;
  }
  if (options.icon) {
    item.iconPath = new vscode.ThemeIcon(options.icon);
  }
  return item;
}

async function openPanel(panelRegistry, kind) {
  try {
    await panelRegistry.openGeneric(kind);
  } catch (error) {
    vscode.window.showErrorMessage(toErrorMessage(error));
  }
}

module.exports = {
  activate,
  deactivate,
};
