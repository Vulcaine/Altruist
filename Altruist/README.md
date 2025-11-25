# Altruist Framework

Altruist is a high-performance framework for real-time systems that keeps infrastructure boring and product code fast. It delivers a single flow for building web apps, WebSockets, TCP/UDP servers, and game backends without bolting together disparate stacks.

## Why Altruist

- **Built-in dependency injection:** Attribute-driven service discovery (e.g., ServiceAttribute) wires classes automatically, while ServiceConfigurationAttribute lets you layer multiple service configurations for environments, tenants, or feature slices without ceremony.
- **One pipeline for many transports:** Spin up HTTP, WebSocket, TCP, and UDP endpoints with the same builder pattern, so gameplay loops, chat, telemetry, and admin APIs all share the same mental model.
- **Performance first:** Engine-friendly scheduling, efficient serialization, and persistence hooks keep latency low even with heavy concurrency.
- **Data and cache ready:** Redis and Postgres work out of the box, and the provider model keeps other databases or caches trivial to plug in.
- **Portal pattern for gameplay:** Drop-in portals give you session, room, and movement flows without losing control of the game logic.

## Quick Start

A single entrypoint plus a config file spins up the full stack.

```csharp
using Altruist;

public static class Program
{
    public static async Task Main(string[] args)
        => await AltruistApplication.Run(args);
}
```

### Configure everything in `config.yml`

Drop a config alongside your app and Altruist wires transports, persistence, and the game loop for you:

```yaml
altruist:
  environment:
    mode: 3D
  security:
    mode: "jwt"

  server:
    http:
      host: "0.0.0.0"
      port: 8000
      path: "/"
    transport:
      mode: websocket
      codec:
        provider: json
      config:
        path: "ws"

  persistence:
    database:
      provider: postgres
      host: localhost
      port: 5432
      username: altruist
      password: altruist
      database: altruist
    cache:
      provider: "inmemory"

  game:
    engine:
      diagnostics: true
      frequency: 30
      unit: "ticks"
      gravity: { x: 0, y: -9.81 }
    worlds:
      partitioner: { width: 64, height: 64, depth: 64 }
      items:
        - index: 0
          size: { x: 100, y: 100 }
          gravity: { x: 0, y: 0 }
          position: { x: 0, y: 0 }
```

- **Single command to run:** `dotnet run` picks up your config and boots HTTP + WebSocket + game engine.
- **Swap providers fast:** change the YAML to switch codecs, transports, or persistence without touching code.
- **Game-first defaults:** worlds, gravity, and engine frequency live in config for quick iteration.

## Define portals and gates

Altruist portals map routes and permissions through attributes so gameplay flows stay declarative:

```csharp
using Altruist;
using Altruist.Gaming;
using Altruist.Security;

[SessionShield]
[Portal("/game")]
public class GameSessionPortal : AltruistGameSessionPortal
{
    [Gate("available-servers")]
    public Task<IResultPacket> AvailableServersAsync(string clientId) { ... }
}
```

- **[Portal]** marks the route, **[Gate]** marks invokable actions, **[SessionShield]** enforces auth.
- Inherit from the base portal to get session and routing plumbing; you focus on gameplay logic only.

## HTTP flows stay standard

Two-way gameplay uses portals; one-way HTTP APIs keep the familiar ASP.NET Core controllers. You can still inherit from helpers like `JwtAuthController`, use `[ApiController]` + `[Route]`, and inject Altruist services (e.g., `ILoginService`, `IJwtTokenValidator`) through the DI container. Your controllers and Altruist portals coexist—WebSocket/game traffic flows through portals, while REST endpoints behave exactly as in a normal ASP.NET Core app.
