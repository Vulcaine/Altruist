const SUMMARY_PATH = "/dashboard/v1/summary";
const WORLDS_PATH = "/dashboard/v1/worlds";
const SESSIONS_PATH = "/dashboard/v1/sessions";
const CACHE_PATH = "/dashboard/v1/cache";
const VAULTS_PATH = "/dashboard/v1/vaults";
const NETWORK_PATH = "/dashboard/v1/network";
const PERFORMANCE_PATH = "/dashboard/v1/performance";
const LAB_PATH = "/dashboard/v1/lab";

class DashboardClient {
  constructor(baseUrl) {
    this.baseUrl = normalizeBaseUrl(baseUrl);
  }

  async getSummary() {
    return this.#fetchJson(SUMMARY_PATH);
  }

  async updateConfig(key, value) {
    return this.#fetchJson(`${SUMMARY_PATH}/config/update`, {
      method: "POST",
      body: JSON.stringify({ key, value }),
    });
  }

  async updateConfigBatch(entries) {
    return this.#fetchJson(`${SUMMARY_PATH}/config/update-batch`, {
      method: "POST",
      body: JSON.stringify(entries || []),
    });
  }

  async getWorlds() {
    return this.#fetchJson(WORLDS_PATH, { cache: "no-store" });
  }

  async getWorldObjectsSnapshot(worldIndex) {
    return this.#fetchJson(
      `${WORLDS_PATH}/${encodeURIComponent(worldIndex)}/objects`,
      { cache: "no-store" }
    );
  }

  async getWorldGizmos(worldIndex) {
    return this.#fetchJson(
      `${WORLDS_PATH}/${encodeURIComponent(worldIndex)}/gizmos`,
      { cache: "no-store" }
    );
  }

  async getSessions() {
    return this.#fetchJson(SESSIONS_PATH);
  }

  async getConnections() {
    return this.#fetchJson(`${SESSIONS_PATH}/connections`);
  }

  async closeSession(connectionId) {
    return this.#fetchVoid(
      `${SESSIONS_PATH}/connections/${encodeURIComponent(connectionId)}`,
      { method: "DELETE" }
    );
  }

  async deleteRoom(roomId) {
    return this.#fetchVoid(`${SESSIONS_PATH}/rooms/${encodeURIComponent(roomId)}`, {
      method: "DELETE",
    });
  }

  async removeConnectionFromRoom(roomId, connectionId) {
    return this.#fetchVoid(
      `${SESSIONS_PATH}/rooms/${encodeURIComponent(roomId)}/connections/${encodeURIComponent(connectionId)}`,
      { method: "DELETE" }
    );
  }

  async getCacheInfo() {
    return this.#fetchJson(`${CACHE_PATH}/info`);
  }

  async getCacheEntries() {
    return this.#fetchNdjson(`${CACHE_PATH}/entries/stream`);
  }

  async updateCacheEntry(entry) {
    return this.#fetchVoid(`${CACHE_PATH}/entry`, {
      method: "PUT",
      body: JSON.stringify({
        type: entry.type,
        groupId: entry.groupId ?? "",
        key: entry.key,
        value: entry.value,
      }),
    });
  }

  async deleteCacheEntry(entry) {
    const params = new URLSearchParams({
      type: entry.type,
      groupId: entry.groupId ?? "",
      key: entry.key,
    });

    return this.#fetchVoid(`${CACHE_PATH}/entry?${params.toString()}`, {
      method: "DELETE",
    });
  }

  async getVaults() {
    return this.#fetchJson(VAULTS_PATH);
  }

  async getNetworkEvents(options = {}) {
    const params = new URLSearchParams();
    if (options.sinceId) params.set("sinceId", String(options.sinceId));
    if (options.take) params.set("take", String(options.take));
    if (options.kind) params.set("kind", options.kind);
    if (options.direction) params.set("direction", options.direction);
    if (options.query) params.set("query", options.query);

    const suffix = params.toString() ? `?${params.toString()}` : "";
    return this.#fetchJson(`${NETWORK_PATH}/events${suffix}`, { cache: "no-store" });
  }

  async getPerformance() {
    return this.#fetchJson(PERFORMANCE_PATH, { cache: "no-store" });
  }

  async getLabActions() {
    return this.#fetchJson(`${LAB_PATH}/actions`, { cache: "no-store" });
  }

  async invokeLabAction(action) {
    return this.#fetchJson(`${LAB_PATH}/invoke`, {
      method: "POST",
      body: JSON.stringify(action),
    });
  }

  async getVaultItems(typeKey, skip = 0, take = 50) {
    const params = new URLSearchParams({
      skip: String(skip),
      take: String(take),
    });

    return this.#fetchJson(
      `${VAULTS_PATH}/${encodeURIComponent(typeKey)}/items?${params.toString()}`
    );
  }

  async batchUpdateVault(typeKey, items) {
    return this.#fetchJson(
      `${VAULTS_PATH}/${encodeURIComponent(typeKey)}/batch-update`,
      {
        method: "POST",
        body: JSON.stringify({
          typeKey,
          items,
        }),
      }
    );
  }

  async queryVault(typeKey, sql) {
    return this.#fetchJson(
      `${VAULTS_PATH}/${encodeURIComponent(typeKey)}/query`,
      {
        method: "POST",
        body: JSON.stringify({ sql }),
      }
    );
  }

  async #fetchJson(path, init = {}) {
    const response = await fetch(`${this.baseUrl}${path}`, {
      ...init,
      headers: {
        "Content-Type": "application/json",
        ...(init.headers ?? {}),
      },
    });

    if (!response.ok) {
      throw new Error(await buildErrorMessage(response));
    }

    return response.json();
  }

  async #fetchVoid(path, init = {}) {
    const response = await fetch(`${this.baseUrl}${path}`, {
      ...init,
      headers: {
        "Content-Type": "application/json",
        ...(init.headers ?? {}),
      },
    });

    if (!response.ok) {
      throw new Error(await buildErrorMessage(response));
    }
  }

  async #fetchNdjson(path) {
    const response = await fetch(`${this.baseUrl}${path}`, {
      cache: "no-store",
    });

    if (!response.ok) {
      throw new Error(await buildErrorMessage(response));
    }

    const text = await response.text();
    return text
      .split(/\r?\n/g)
      .map((line) => line.trim())
      .filter(Boolean)
      .map((line) => JSON.parse(line));
  }
}

function normalizeBaseUrl(baseUrl) {
  return String(baseUrl || "").trim().replace(/\/+$/, "");
}

async function buildErrorMessage(response) {
  const body = await response.text();
  if (!body) {
    return `Request failed with ${response.status} ${response.statusText}`.trim();
  }

  return `Request failed with ${response.status} ${response.statusText}: ${body}`.trim();
}

module.exports = {
  DashboardClient,
  normalizeBaseUrl,
};
