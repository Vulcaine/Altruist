# Altruist Framework

Altruist is a powerful, fast, and elegant game server framework designed for building game infrastructure effortlessly. It's optimized for real-time operations but is flexible enough for a wide range of applications. With Altruist, you can set up your whole infrastructure, including transport, database, caching, and more, with minimal effort.

## Key Features

### 1. **Powerful Engine Optimized for Real-Time Operations**

Altruist comes with a high-performance engine optimized for real-time processing, making it ideal for game servers and other real-time applications. The engine is highly efficient, capable of handling numerous connections and tasks with minimal overhead.

- **Frame Rates**: Easily control the engine’s frame rate with predefined options like `FrameRate.Hz30` for consistent task execution.

### 2. **Cycle Attribute for Method Execution Control**

The Cycle Attribute enables you to define and control method execution rates within the engine. You can specify tasks that should execute at a higher rate, such as `Hz60` for more frequent updates or background tasks that need lower rates. This allows for flexible task scheduling and optimizes performance for various needs, from game mechanics to background calculations.

### 3. **Plug-and-Play Support with Portals**

Altruist allows you to create and share custom portals, making it easy to plug new features into the framework. A portal is a communication layer that connects different game components or systems, and Altruist provides an easy way to set them up and integrate them into your game server.

- **Custom Portals**: You can create your own portals for various functionalities such as game mechanics, chat systems, or other server-side tasks.
- **Relay Portals**: Automatically handle relays between different game components or servers for efficient communication.

### 4. **Plug-and-Play Caching and Database Support**

Altruist comes with built-in support for various caching and database systems. Currently, it supports Redis for caching and ScyllaDB for persistence. The framework allows for plug-and-play integration with your preferred database and caching services.

- **Redis**: Use Redis for fast in-memory caching.
- **ScyllaDB**: Use ScyllaDB for persistent storage with high throughput and low latency.
- **Session-Based Games**: By default, Altruist uses in-memory caching for session-based games where persistence is not required.

### 5. **Easy Object Mapping for Persistence Providers**

The Altruist framework provides easy-to-use object mapping, making it simple to work with entities and manage their persistence. You can integrate with any future persistence provider with ease.

- **Auto-Mapper**: Automatically map your objects to persistence layers without needing boilerplate code.
- **Historical Entity Saves/Updates**: Altruist automatically handles saving and updating historical entity states, allowing you to track changes over time.

### 6. **Built-In Features for Game Development**

Altruist comes with several built-in features for game development, making it easier to create common gameplay mechanics.

- **Movement Portals**: Pre-built portals for common movement patterns, such as forward-only or 8-directional movement. This allows for faster game development without reinventing the wheel.
- **AutoSave Portal**: Built-in support for automatic saving of game data with configurable save strategies, ensuring players' progress is always stored securely.

## Example API Usage

Here’s how you can quickly set up your game server infrastructure using Altruist:

```csharp
AltruistBuilder.Create(args)
    .SetupTransport<WebSocketConnectionSetup>(WebSocketTransportToken.Instance, (setup) =>
    {
        return setup
            .MapPortal<SpaceshipGamePortal>("/game")
            .MapPortal<RegenPortal>("/game")
            .MapRelayPortal<MovementRelayPortal>("localhost", Config.MOVEMENT, "sync-movement");
    })
    .SetupDatabase<ScyllaDBConnectionSetup>(ScyllaDBToken.Instance, (setup) =>
    {
        return setup
            .AddContactPoint("localhost", 9042)
            .CreateKeyspace<DefaultScyllaKeyspace>(setup => setup.AddVault<Player>());
    })
    .UseCache<RedisConnectionSetup>(RedisCacheServiceToken.Instance)
    .EnableEngine(FrameRate.Hz30)
    .StartServer();
