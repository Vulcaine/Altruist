# Altruist VS Code Dashboard Extension

Native VS Code client for the existing Altruist dashboard backend.

## What it does

- Adds an **Altruist** activity bar entry.
- Shows **Connection** and **Dashboard** navigation views in the left sidebar.
- Opens dashboard experiences in editor webviews:
  - Summary
  - Worlds
  - Config
  - Sessions
  - Cache
  - Vault
- Reuses the existing runtime dashboard endpoints and websocket stream.

## Important behavior

- This extension is **dashboard-backed only**.
- It does **not** attach directly to the process.
- It expects `altruist:dashboard:enabled = true` on the running server.
- The world viewer currently supports **3D environments only**.

## Connection model

The extension tries to discover the dashboard endpoint from workspace config files such as:

- `config.yml`
- `config.yaml`
- `config.*.yml`
- `config.*.yaml`

If auto-discovery is not enough, you can override these values in VS Code settings:

- `altruist.dashboard.baseUrl`
- `altruist.dashboard.websocketUrl`
- `altruist.dashboard.autoConnect`
- `altruist.dashboard.defaultWorldIndex`

## World view

The world panel is a native VS Code webview renderer that:

- loads worlds from `/dashboard/v1/worlds`
- loads snapshots from `/dashboard/v1/worlds/{worldIndex}/objects`
- connects to the dashboard websocket stream
- renders terrain wireframes, objects, colliders, and partition overlays
- lets you orbit, pan, zoom, click-select, and inspect objects

## Development notes

- The extension host is plain CommonJS (`extension.js`, `lib/*.js`).
- The world webview uses ES modules under `media/`.
- Three.js is vendored under `media/vendor/three.module.js`.
