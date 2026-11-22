# AltruistProject

An [Altruist](https://altruist-docs.vercel.app) game server. `dotnet run` starts it on port 8080, in the Development environment
(`Properties/launchSettings.json`):

- `Portals/HelloPortal.cs`: a WebSocket portal at `ws://localhost:8080/`; its `[Gate]` methods handle client packets.
- `Http/HelloController.cs`: an ASP.NET Core controller at `/api/hello`. The framework adds `/altruist/health` itself.
- `config.yml`: the framework's settings. `config.{environment}.yml`, `ALTRUIST__...` environment variables and
  command-line arguments override it, in that order.

## Before writing server code

Altruist already has most systems a game server needs: transports, auth and sessions, persistence, rooms,
matchmaking, lobbies, worlds, physics, AI and typed clients. Look the system up and use it instead of writing your
own handler, repository or auth.

The Altruist CLI serves the catalog of these systems over MCP, with their config keys, defaults and examples:

```sh
dotnet tool install -g Altruist.CLI
claude mcp add altruist -- altruist mcp
```

Its tools are `altruist_search` (by a word, type or config key), `altruist_capabilities` (the full list) and
`altruist_capability` (one system in detail).

## Looking at the running server

The same MCP server reads the running server through its dashboard API: `altruist_runtime` (config, connections,
rooms, engine, worlds, vaults, cache, routes, latency), `altruist_runtime_world` (a world's objects),
`altruist_runtime_vault` (stored rows) and `altruist_runtime_traffic` (recent packets and requests). They only read.
Turn the dashboard on for development: add its package, and enable it in a `config.Development.yml` next to
`config.yml`:

```sh
dotnet add package Altruist.Dashboard --prerelease
```

```yaml
altruist:
  dashboard:
    enabled: true
```

In Development the dashboard answers requests from this machine without a token. The tools look for the server at
`http://localhost:8080`; set `ALTRUIST_DASHBOARD_URL` for another address, and `ALTRUIST_DASHBOARD_TOKEN` when the
server has an `altruist:dashboard:token`:

```sh
claude mcp add altruist -e ALTRUIST_DASHBOARD_URL=http://localhost:9000 -- altruist mcp
```
