# AltruistProject

An [Altruist](https://altruist-docs.vercel.app) game server. `dotnet run` starts it on port 8080:

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
