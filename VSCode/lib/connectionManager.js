const vscode = require("vscode");
const { discoverConnectionInfo } = require("./configDiscovery");

const TOKEN_HEADER = "X-Altruist-Dashboard-Token";

/**
 * Where the dashboard is and how to reach it: the server address (settings or the workspace's config.yml), the
 * dashboard token (kept in VS Code's secret storage, per server), and the features the server reported.
 */
class ConnectionManager {
  constructor(context, output) {
    this.context = context;
    this.output = output;
    this.state = {
      connected: false,
      baseUrl: "",
      source: "none",
      configPath: undefined,
      dashboardEnabled: false,
      features: [],
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

  async autoConnectIfEnabled() {
    if (!vscode.workspace.getConfiguration("altruist.dashboard").get("autoConnect", true)) return;
    const discovered = await discoverConnectionInfo();
    if (!discovered.baseUrl || (!discovered.dashboardEnabled && discovered.source !== "settings")) return;
    try {
      await this.connect(false);
    } catch (error) {
      this.output.appendLine(`[auto-connect] ${toErrorMessage(error)}`);
    }
  }

  /** Finds the server and checks it answers; remembers the dashboard areas it serves. */
  async connect(showFeedback = true) {
    const discovered = await discoverConnectionInfo();
    this.state.baseUrl = discovered.baseUrl || "";
    this.state.source = discovered.source || "none";
    this.state.configPath = discovered.configPath;
    this.state.dashboardEnabled = Boolean(discovered.dashboardEnabled);

    if (!this.state.baseUrl) {
      throw new Error("No Altruist server was found. Set altruist.dashboard.baseUrl or open a workspace with a config.yml.");
    }
    if (!this.state.dashboardEnabled && discovered.source !== "settings") {
      throw new Error("The dashboard is not enabled in the workspace config (altruist:dashboard:enabled).");
    }

    const response = await this.fetch("/dashboard/v1/summary");
    if (!response.ok) throw new Error(await describeFailure(response));
    const summary = await response.json();

    this.state.connected = true;
    this.state.features = Array.isArray(summary.features) ? summary.features : [];
    this.state.lastError = undefined;
    this._emitter.fire();
    if (showFeedback) vscode.window.showInformationMessage(`Connected to the Altruist dashboard at ${this.state.baseUrl}.`);
    return this.getState();
  }

  disconnect(showFeedback = true) {
    this.state.connected = false;
    this.state.features = [];
    this.state.lastError = undefined;
    this._emitter.fire();
    if (showFeedback) vscode.window.showInformationMessage("Disconnected from the Altruist dashboard.");
  }

  async ensureConnected() {
    return this.state.connected ? this.getState() : this.connect(false);
  }

  setLastError(error) {
    this.state.connected = false;
    this.state.lastError = toErrorMessage(error);
    this._emitter.fire();
  }

  /** A request to the server with the dashboard token, when one is set. */
  async fetch(path, init = {}) {
    const token = await this.getToken();
    const headers = { ...(init.headers || {}) };
    if (token) headers[TOKEN_HEADER] = token;
    return fetch(`${this.state.baseUrl}${path}`, { ...init, headers, cache: "no-store" });
  }

  tokenKey() {
    return `altruist.dashboard.token:${this.state.baseUrl || "default"}`;
  }

  async getToken() {
    return (await this.context.secrets.get(this.tokenKey())) || "";
  }

  async setToken(token) {
    if (token) await this.context.secrets.store(this.tokenKey(), token);
    else await this.context.secrets.delete(this.tokenKey());
  }
}

async function describeFailure(response) {
  const body = await response.text().catch(() => "");
  if (response.status === 401) return "The server wants a dashboard token: run 'Altruist: Set Dashboard Token'.";
  if (response.status === 503) return "The dashboard is closed on this server: configure altruist:dashboard:token (or a policy).";
  return `The server answered ${response.status} ${response.statusText}${body ? `: ${body}` : ""}`;
}

function toErrorMessage(error) {
  return error instanceof Error ? error.message : String(error);
}

module.exports = { ConnectionManager, describeFailure, toErrorMessage };
