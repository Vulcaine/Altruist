# Altruist Framework

[Read The Docs](https://altruist-docs.vercel.app)

Altruist is a high-performance game server framework for real-time applications. It simplifies infrastructure setup, offering easy integration for transport, database, caching, and game mechanics.

# Key Features
- Optimized for Real-Time: Handles many concurrent connections with minimal overhead.

- **Cycle Attribute:** Control method execution rates (e.g., Hz30, Hz60).

- **Plug-and-Play Portals:** Easily create and integrate custom portals for game mechanics, chat, and more.

- **Built-In Caching & Database:** Integrates Redis for caching and ScyllaDB for persistent storage.

- **Auto Object Mapping:** Automatically map objects to persistence layers.

- **Game Tools:** Pre-built portals for movement, session management, and auto-saving.

## Quick Start

Here’s how you can quickly set up your game server infrastructure using Altruist:

```csharp
using Altruist;
using Altruist.Redis;
using Altruist.Web;
using Portals;

AltruistBuilder.Create(args, serviceBuilder => serviceBuilder.AddGamingSupport())
    .NoEngine()
    .WithWebsocket(setup => setup.MapPortal<SimpleGamePortal>("/game"))
    .WebApp()
    .StartServer();
```

### Steps:
- **WebSocket:** Configure transport and map portals.

- **Redis:** Add documents for caching and persistence.

- **ScyllaDB:** Integrate with a high-speed scalable database.

- **Start:** Launch your server with .StartServer().

All you left to do is setting up the redis / scylladb server that Altruist can connect to. :)

## Create your portal

```csharp
namespace GameGateway.Portals
{
    public class SpaceshipGamePortal : AltruistGameSessionPortal<SpaceshipPlayer>
    {
        ...
    }
}
```

The SpaceshipGamePortal inherits from AltruistSpaceshipGamePortal. This base class provides common functionalities for handling spaceship-related game logic, like joining the game or processing interactions.

When you create a portal like this, it automatically enables functionalities like room management, player session management, and the basic communication flow between the client and server. The only thing you need to know is which portal you have to plug in :)
