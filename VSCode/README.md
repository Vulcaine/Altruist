# Altruist Dashboard for VS Code

The Altruist dashboard inside VS Code: the same app the server serves at `/altruist/dashboard`, in an editor tab.

- Server and fleet, the fleet's architecture, engine, worlds (2D and 3D, live), rooms, AI and zones, pairing, connections.
- Configuration, cache, autosave, vaults (rows, schema, SQL).
- Network traffic, latency percentiles, and an API lab with saved collections.

The sidebar (**Altruist** in the activity bar) shows the connection and the views the server serves.

## Connecting

The extension needs a running server with `altruist:dashboard:enabled: true`. It finds the server from:

1. the `altruist.dashboard.baseUrl` setting (for example `http://localhost:8000`), else
2. the workspace's `config.yml` (the one that enables the dashboard; its `altruist:server:http` host, port and path).

A server with `altruist:dashboard:token` needs that token: run **Altruist: Set Dashboard Token** (it is kept in VS
Code's secret storage, per server). Without a token a server answers only on loopback in Development.

`altruist.dashboard.autoConnect` (on by default) connects when a workspace with such a config opens.

## Commands

- `Altruist: Open Dashboard`, `Altruist: Open Worlds`, `Altruist: Open API Lab`
- `Altruist: Connect to Dashboard`, `Altruist: Reconnect`, `Altruist: Disconnect Dashboard`
- `Altruist: Set Dashboard Token`, `Altruist: Clear Dashboard Token`

## Development

The extension host is plain CommonJS (`extension.js`, `lib/`). The webview is the dashboard app from
`../Altruist/Dashboard/App`: build it there (`npm run build`), then `npm run copy-app` here copies it into
`media/app` (packaging does this by itself through `vscode:prepublish`). Every request from the webview goes through
`lib/panel.js`, which adds the server address and the token.
