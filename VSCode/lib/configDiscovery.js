const vscode = require("vscode");

async function discoverConnectionInfo() {
  const config = vscode.workspace.getConfiguration("altruist.dashboard");

  const baseUrlOverride = String(config.get("baseUrl") || "").trim();
  const websocketUrlOverride = String(config.get("websocketUrl") || "").trim();

  if (baseUrlOverride) {
    return {
      source: "settings",
      dashboardEnabled: true,
      environmentMode: undefined,
      baseUrl: normalizeBaseUrl(baseUrlOverride),
      websocketUrl:
        websocketUrlOverride || deriveWebsocketUrlFromBaseUrl(baseUrlOverride),
      configPath: undefined,
    };
  }

  const configPath = await findWorkspaceConfigPath();
  if (!configPath) {
    return {
      source: "none",
      dashboardEnabled: false,
      environmentMode: undefined,
      baseUrl: "",
      websocketUrl: websocketUrlOverride,
      configPath: undefined,
    };
  }

  const bytes = await vscode.workspace.fs.readFile(vscode.Uri.file(configPath));
  const text = Buffer.from(bytes).toString("utf8");
  const parsed = parseSimpleYaml(text);

  const dashboardEnabled =
    toBoolean(parsed?.altruist?.dashboard?.enabled) === true;
  const environmentMode = stringOrUndefined(parsed?.altruist?.environment?.mode);

  const httpHost = normalizeHost(
    stringOrUndefined(parsed?.altruist?.server?.http?.host) || "localhost"
  );
  const httpPort = Number(parsed?.altruist?.server?.http?.port || 8000);
  const httpPath = normalizePath(
    stringOrUndefined(parsed?.altruist?.server?.http?.path) || "/"
  );

  const transportPath = normalizePath(
    stringOrUndefined(parsed?.altruist?.server?.transport?.config?.path) || "/ws"
  );

  const baseUrl = normalizeBaseUrl(`http://${httpHost}:${httpPort}${httpPath}`);
  const websocketUrl =
    websocketUrlOverride ||
    normalizeWebsocketUrl(`ws://${httpHost}:${httpPort}${transportPath}/dashboard`);

  return {
    source: "workspace",
    dashboardEnabled,
    environmentMode,
    baseUrl,
    websocketUrl,
    configPath,
  };
}

async function findWorkspaceConfigPath() {
  const matches = await vscode.workspace.findFiles(
    "**/{config.yml,config.yaml,config.*.yml,config.*.yaml}",
    "**/{node_modules,bin,obj,.git}/**",
    20
  );

  if (!matches.length) return undefined;

  const sorted = matches
    .map((uri) => uri.fsPath)
    .sort((a, b) => scoreConfigPath(a) - scoreConfigPath(b));

  return sorted[0];
}

function scoreConfigPath(filePath) {
  const lower = filePath.replace(/\\/g, "/").toLowerCase();
  if (lower.endsWith("/config.yml") || lower.endsWith("/config.yaml")) return 0;
  if (lower.includes("/examples/")) return 50;
  return 10;
}

function parseSimpleYaml(text) {
  const root = {};
  const stack = [{ indent: -1, value: root }];

  const lines = text.split(/\r?\n/g);
  for (const rawLine of lines) {
    if (!rawLine.trim() || rawLine.trimStart().startsWith("#")) continue;
    if (rawLine.trimStart().startsWith("- ")) continue;

    const match = rawLine.match(/^(\s*)([^:#]+):(.*)$/);
    if (!match) continue;

    const indent = match[1].length;
    const key = match[2].trim();
    const rawValue = match[3].trim();

    while (stack.length > 1 && indent <= stack[stack.length - 1].indent) {
      stack.pop();
    }

    const parent = stack[stack.length - 1].value;

    if (!rawValue) {
      const next = {};
      parent[key] = next;
      stack.push({ indent, value: next });
      continue;
    }

    parent[key] = parseScalar(rawValue);
  }

  return root;
}

function parseScalar(value) {
  const trimmed = value.trim();
  const unquoted = trimmed.replace(/^["']|["']$/g, "");
  if (/^(true|false)$/i.test(unquoted)) return /^true$/i.test(unquoted);
  if (/^-?\d+(\.\d+)?$/.test(unquoted)) return Number(unquoted);
  return unquoted;
}

function stringOrUndefined(value) {
  if (value === undefined || value === null) return undefined;
  return String(value);
}

function toBoolean(value) {
  if (typeof value === "boolean") return value;
  if (typeof value === "string") return value.toLowerCase() === "true";
  return undefined;
}

function normalizeHost(host) {
  if (!host || host === "0.0.0.0" || host === "::" || host === "[::]") {
    return "localhost";
  }
  return host;
}

function normalizePath(path) {
  const normalized = String(path || "/").trim();
  if (!normalized) return "/";
  return normalized.startsWith("/") ? normalized.replace(/\/+$/, "") || "/" : `/${normalized.replace(/\/+$/, "")}`;
}

function normalizeBaseUrl(baseUrl) {
  return String(baseUrl || "").trim().replace(/\/+$/, "");
}

function normalizeWebsocketUrl(url) {
  return String(url || "").trim().replace(/\/+$/, "");
}

function deriveWebsocketUrlFromBaseUrl(baseUrl) {
  const normalized = normalizeBaseUrl(baseUrl);
  if (!normalized) return "";

  if (normalized.startsWith("https://")) {
    return normalized.replace(/^https:\/\//i, "wss://") + "/ws/dashboard";
  }

  return normalized.replace(/^http:\/\//i, "ws://") + "/ws/dashboard";
}

module.exports = {
  discoverConnectionInfo,
  normalizeBaseUrl,
  normalizeWebsocketUrl,
};
