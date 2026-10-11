const vscode = require("vscode");
const { describeFailure, toErrorMessage } = require("./connectionManager");

const STATE_PREFIX = "altruist.dashboard.state.";

/**
 * The dashboard in a webview: the same app the server serves at /altruist/dashboard (media/app, copied from
 * Altruist/Dashboard/App/dist). The webview cannot reach the server or hold the token, so every call is a message;
 * this side performs it (src/host/bridge.ts in the app describes the messages).
 */
class DashboardPanel {
  constructor(context, connection, output) {
    this.context = context;
    this.connection = connection;
    this.output = output;
    this.panel = undefined;
    this.ready = false;
    this.pendingRoute = undefined;
    this.streams = new Map();
  }

  /** Shows the dashboard at `route` (a view id such as "worlds", optionally with a sub-path). */
  async open(route) {
    try {
      await this.connection.ensureConnected();
    } catch (error) {
      this.connection.setLastError(error);
      vscode.window.showErrorMessage(toErrorMessage(error));
      return;
    }
    if (!this.panel) this.create();
    this.panel.reveal(vscode.ViewColumn.Active);
    if (!route) return;
    if (this.ready) this.post({ type: "navigate", route: `#/${route}` });
    else this.pendingRoute = route;
  }

  create() {
    const appRoot = vscode.Uri.joinPath(this.context.extensionUri, "media", "app");
    this.panel = vscode.window.createWebviewPanel("altruist.dashboard", "Altruist", vscode.ViewColumn.Active, {
      enableScripts: true,
      retainContextWhenHidden: true,
      localResourceRoots: [appRoot],
    });
    this.panel.iconPath = vscode.Uri.joinPath(this.context.extensionUri, "media", "icon.png");
    this.panel.webview.html = this.html(appRoot);
    this.panel.webview.onDidReceiveMessage((message) => this.handle(message), undefined, this.context.subscriptions);
    this.panel.onDidDispose(() => {
      for (const stream of this.streams.values()) stream.abort();
      this.streams.clear();
      this.panel = undefined;
      this.ready = false;
    }, undefined, this.context.subscriptions);
  }

  /**
   * The app's index.html with its assets pointed into the extension and a CSP that only allows its own files. No
   * <base>: the app's "#/view" links would then leave the webview.
   */
  html(appRoot) {
    const fs = require("fs");
    const path = require("path");
    let index;
    try {
      index = fs.readFileSync(path.join(appRoot.fsPath, "index.html"), "utf8");
    } catch {
      return "<p>The dashboard app is missing from the extension (media/app). Build Altruist/Dashboard/App and package again.</p>";
    }
    const webview = this.panel.webview;
    const assets = `${webview.asWebviewUri(vscode.Uri.joinPath(appRoot, "assets"))}/`;
    const csp = [
      "default-src 'none'",
      `img-src ${webview.cspSource} data:`,
      `font-src ${webview.cspSource}`,
      `style-src ${webview.cspSource} 'unsafe-inline'`,
      `script-src ${webview.cspSource}`,
      "worker-src blob:",
    ].join("; ");
    return index
      .replaceAll("./assets/", assets)
      .replace("<head>", `<head>\n    <meta http-equiv="Content-Security-Policy" content="${csp}" />`);
  }

  post(message) {
    return this.panel?.webview.postMessage(message);
  }

  async handle(message) {
    switch (message?.type) {
      case "ready":
        this.ready = true;
        this.post({ type: "server", label: this.connection.getState().baseUrl });
        if (this.pendingRoute) {
          this.post({ type: "navigate", route: `#/${this.pendingRoute}` });
          this.pendingRoute = undefined;
        }
        return;
      case "json":
        return this.json(message.id, message.request);
      case "lines":
        return this.lines(message.id, message.path);
      case "cancel":
        this.streams.get(message.id)?.abort();
        this.streams.delete(message.id);
        return;
      case "state-get":
        this.post({ type: "result", id: message.id, value: this.context.workspaceState.get(STATE_PREFIX + message.key) });
        return;
      case "state-set":
        await this.context.workspaceState.update(STATE_PREFIX + message.key, message.value);
        this.post({ type: "result", id: message.id, value: null });
        return;
      case "socket-open":
        // The dashboard follows worlds over HTTP streams; no socket is used.
        this.post({ type: "socket-state", open: false });
        return;
      default:
        return;
    }
  }

  async json(id, request) {
    try {
      const response = await this.connection.fetch(request.path, {
        method: request.method,
        headers: request.body === undefined ? undefined : { "Content-Type": "application/json" },
        body: request.body === undefined ? undefined : JSON.stringify(request.body),
      });
      if (!response.ok) {
        this.post({ type: "failure", id, status: response.status, message: await describeFailure(response) });
        return;
      }
      const text = await response.text();
      this.post({ type: "result", id, value: text ? JSON.parse(text) : null });
    } catch (error) {
      this.output.appendLine(`[request] ${request.method} ${request.path}: ${toErrorMessage(error)}`);
      this.post({ type: "failure", id, status: 0, message: `The server is not reachable (${toErrorMessage(error)}).` });
    }
  }

  async lines(id, path) {
    const abort = new AbortController();
    this.streams.set(id, abort);
    try {
      const response = await this.connection.fetch(path, { signal: abort.signal });
      if (!response.ok) {
        this.post({ type: "failure", id, status: response.status, message: await describeFailure(response) });
        return;
      }
      const decoder = new TextDecoder();
      let rest = "";
      for await (const chunk of response.body) {
        const lines = (rest + decoder.decode(chunk, { stream: true })).split(/\r?\n/);
        rest = lines.pop() || "";
        for (const line of lines) {
          if (line.trim()) this.post({ type: "line", id, item: JSON.parse(line) });
        }
      }
      if (rest.trim()) this.post({ type: "line", id, item: JSON.parse(rest) });
      this.post({ type: "end", id });
    } catch (error) {
      if (!abort.signal.aborted) this.post({ type: "failure", id, status: 0, message: toErrorMessage(error) });
    } finally {
      this.streams.delete(id);
    }
  }
}

module.exports = { DashboardPanel };
