const vscode = require("vscode");
const { DashboardClient } = require("./dashboardClient");
const { discoverConnectionInfo } = require("./configDiscovery");

class ConnectionManager {
  constructor(outputChannel) {
    this.outputChannel = outputChannel;
    this.state = {
      connected: false,
      dashboardEnabled: false,
      baseUrl: "",
      websocketUrl: "",
      source: "none",
      environmentMode: undefined,
      configPath: undefined,
      summary: undefined,
      lastError: undefined,
    };
    this._emitter = new vscode.EventEmitter();
  }

  get onDidChange() {
    return this._emitter.event;
  }

  getState() {
    return { ...this.state };
  }

  getClient() {
    if (!this.state.baseUrl) {
      throw new Error("Dashboard base URL is not configured.");
    }
    return new DashboardClient(this.state.baseUrl);
  }

  async autoConnectIfEnabled() {
    const autoConnect = vscode.workspace
      .getConfiguration("altruist.dashboard")
      .get("autoConnect", true);
    if (!autoConnect) return;

    const discovered = await discoverConnectionInfo();
    if (!discovered.baseUrl) return;
    if (!discovered.dashboardEnabled && discovered.source !== "settings") return;

    try {
      await this.connect(false);
    } catch (error) {
      this.outputChannel.appendLine(`[auto-connect] ${toErrorMessage(error)}`);
    }
  }

  async connect(showFeedback = true) {
    const discovered = await discoverConnectionInfo();
    this.#applyDiscoveredState(discovered);

    if (!this.state.baseUrl) {
      throw new Error("No Altruist dashboard endpoint could be detected. Set altruist.dashboard.baseUrl or add a workspace config.yml.");
    }

    if (!this.state.dashboardEnabled && discovered.source !== "settings") {
      throw new Error("Altruist dashboard is not enabled in the detected workspace config.");
    }

    const client = new DashboardClient(this.state.baseUrl);
    const summary = await client.getSummary();

    this.state.connected = true;
    this.state.summary = summary;
    this.state.lastError = undefined;
    this.state.environmentMode = readConfigValue(summary.configs, "altruist:environment:mode") || this.state.environmentMode;
    this._emitter.fire();

    if (showFeedback) {
      vscode.window.showInformationMessage(
        `Connected to Altruist dashboard at ${this.state.baseUrl}.`
      );
    }

    return this.getState();
  }

  disconnect(showFeedback = true) {
    this.state.connected = false;
    this.state.summary = undefined;
    this.state.lastError = undefined;
    this._emitter.fire();

    if (showFeedback) {
      vscode.window.showInformationMessage("Disconnected from Altruist dashboard.");
    }
  }

  async refresh() {
    return this.connect(false);
  }

  async ensureConnected() {
    if (this.state.connected && this.state.summary) {
      return this.getState();
    }

    return this.connect(false);
  }

  setLastError(error) {
    this.state.connected = false;
    this.state.lastError = toErrorMessage(error);
    this._emitter.fire();
  }

  #applyDiscoveredState(discovered) {
    this.state.baseUrl = discovered.baseUrl || "";
    this.state.websocketUrl = discovered.websocketUrl || "";
    this.state.dashboardEnabled = Boolean(discovered.dashboardEnabled);
    this.state.source = discovered.source || "none";
    this.state.environmentMode = discovered.environmentMode;
    this.state.configPath = discovered.configPath;
  }
}

function readConfigValue(configs, key) {
  const entry = (configs || []).find((item) => item.key === key);
  return entry?.value ?? undefined;
}

function toErrorMessage(error) {
  if (error instanceof Error) return error.message;
  return String(error);
}

module.exports = {
  ConnectionManager,
  toErrorMessage,
};
