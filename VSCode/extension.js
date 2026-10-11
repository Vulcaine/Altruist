const vscode = require("vscode");
const { ConnectionManager, toErrorMessage } = require("./lib/connectionManager");
const { DashboardPanel } = require("./lib/panel");

/**
 * The dashboard's views (the app's src/app/registry.ts): id, label, icon, and the server area it needs. The sidebar
 * lists the ones the connected server serves.
 */
const VIEWS = [
  { id: "overview", label: "Overview", icon: "dashboard", feature: "summary" },
  { id: "config", label: "Configuration", icon: "settings-gear", feature: "summary" },
  { id: "server", label: "Server & fleet", icon: "server", feature: "server" },
  { id: "architecture", label: "Architecture", icon: "type-hierarchy", feature: "architecture" },
  { id: "engine", label: "Engine", icon: "pulse", feature: "engine" },
  { id: "connections", label: "Connections", icon: "plug", feature: "sessions" },
  { id: "worlds", label: "Worlds", icon: "globe", feature: "worlds" },
  { id: "rooms", label: "Rooms", icon: "organization", feature: "rooms" },
  { id: "gameplay", label: "AI & zones", icon: "hubot", feature: "gameplay" },
  { id: "pairing", label: "Pairing", icon: "device-mobile", feature: "pairing" },
  { id: "cache", label: "Cache", icon: "layers", feature: "cache" },
  { id: "autosave", label: "Autosave", icon: "save-all", feature: "autosave" },
  { id: "vaults", label: "Vaults", icon: "database", feature: "vaults" },
  { id: "network", label: "Network", icon: "arrow-swap", feature: "network" },
  { id: "performance", label: "Performance", icon: "graph", feature: "performance" },
  { id: "lab", label: "API Lab", icon: "beaker", feature: "lab" },
];

// Commands of earlier versions, kept so keybindings and habits still work.
const LEGACY_COMMANDS = {
  "altruist.openSummary": "overview",
  "altruist.openWorlds": "worlds",
  "altruist.openConfig": "config",
  "altruist.openSessions": "connections",
  "altruist.openCache": "cache",
  "altruist.openVault": "vaults",
  "altruist.openNetwork": "network",
  "altruist.openPerformance": "performance",
  "altruist.openLab": "lab",
};

function activate(context) {
  const output = vscode.window.createOutputChannel("Altruist Dashboard");
  const connection = new ConnectionManager(context, output);
  const panel = new DashboardPanel(context, connection, output);
  const connectionTree = new ConnectionTree(connection);
  const viewsTree = new ViewsTree(connection);

  const run = (action) => async () => {
    try {
      await action();
    } catch (error) {
      connection.setLastError(error);
      vscode.window.showErrorMessage(toErrorMessage(error));
    }
  };

  context.subscriptions.push(
    output,
    vscode.window.registerTreeDataProvider("altruistConnection", connectionTree),
    vscode.window.registerTreeDataProvider("altruistExplorer", viewsTree),
    vscode.commands.registerCommand("altruist.connectDashboard", run(() => connection.connect(true))),
    vscode.commands.registerCommand("altruist.refreshConnection", run(() => connection.connect(false))),
    vscode.commands.registerCommand("altruist.disconnectDashboard", () => connection.disconnect(true)),
    vscode.commands.registerCommand("altruist.setToken", run(async () => {
      const token = await vscode.window.showInputBox({
        title: "Altruist dashboard token",
        prompt: `The value of altruist:dashboard:token on ${connection.getState().baseUrl || "the server"} (kept in VS Code's secret storage).`,
        password: true,
        ignoreFocusOut: true,
      });
      if (token === undefined) return;
      await connection.setToken(token.trim());
      await connection.connect(true);
    })),
    vscode.commands.registerCommand("altruist.clearToken", run(async () => {
      await connection.setToken("");
      vscode.window.showInformationMessage("The dashboard token was removed.");
    })),
    vscode.commands.registerCommand("altruist.open", (view) => panel.open(typeof view === "string" ? view : "overview")),
    vscode.commands.registerCommand("altruist.openSettings", () =>
      vscode.commands.executeCommand("workbench.action.openSettings", "@ext:vulcaine.altruist-dashboard-vscode altruist.dashboard")
    ),
    ...Object.entries(LEGACY_COMMANDS).map(([command, view]) => vscode.commands.registerCommand(command, () => panel.open(view))),
    connection.onDidChange(() => {
      connectionTree.refresh();
      viewsTree.refresh();
    })
  );

  connection.autoConnectIfEnabled().catch((error) => output.appendLine(`[activate] ${toErrorMessage(error)}`));
}

function deactivate() {}

class ConnectionTree {
  constructor(connection) {
    this.connection = connection;
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
    const s = this.connection.getState();
    const items = [
      info(s.connected ? "Connected" : s.lastError ? "Not connected" : "Disconnected", s.baseUrl || "no server found", s.connected ? "pass-filled" : s.lastError ? "warning" : "circle-large-outline", s.lastError),
    ];
    if (s.configPath) items.push(info("Config", vscode.workspace.asRelativePath(s.configPath), "file-code", s.configPath));
    if (s.lastError) items.push(info("Error", s.lastError, "error", s.lastError));
    items.push(command(s.connected ? "Reconnect" : "Connect", "altruist.connectDashboard", "plug"));
    items.push(command("Set token", "altruist.setToken", "key"));
    if (s.connected) items.push(command("Disconnect", "altruist.disconnectDashboard", "debug-disconnect"));
    items.push(command("Settings", "altruist.openSettings", "gear"));
    return items;
  }
}

class ViewsTree {
  constructor(connection) {
    this.connection = connection;
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
    const features = new Set(this.connection.getState().features);
    return VIEWS.filter((v) => features.size === 0 || features.has(v.feature)).map((v) => {
      const item = new vscode.TreeItem(v.label, vscode.TreeItemCollapsibleState.None);
      item.iconPath = new vscode.ThemeIcon(v.icon);
      item.command = { command: "altruist.open", title: `Open ${v.label}`, arguments: [v.id] };
      return item;
    });
  }
}

function info(label, description, icon, tooltip) {
  const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
  item.description = description;
  item.tooltip = tooltip || description;
  item.iconPath = new vscode.ThemeIcon(icon);
  return item;
}

function command(label, id, icon) {
  const item = new vscode.TreeItem(label, vscode.TreeItemCollapsibleState.None);
  item.iconPath = new vscode.ThemeIcon(icon);
  item.command = { command: id, title: label };
  return item;
}

module.exports = { activate, deactivate };
