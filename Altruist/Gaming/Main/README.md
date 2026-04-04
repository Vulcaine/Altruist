# Altruist Gaming

Altruist is a high-performance, config-driven server framework for building scalable game servers and real-time applications in C# / .NET 9. While it serves as a general-purpose application server (DI, REST APIs, WebSocket/TCP/UDP, auth, persistence), its standout feature is a complete **game simulation engine** — built-in AI, combat, collision detection, visibility tracking, and automatic entity synchronization — all benchmarked with BenchmarkDotNet.

- **0.9 ms total framework overhead** per tick (97.3% budget available for game logic)
- **AI FSM**: 14 ns/entity, zero allocations
- **Combat**: 8 ns single attack, AoE sweep queries (sphere/cone/line)
- **Collision**: SpatialHashGrid broadphase, 13 μs for 100 entities
- **Visibility**: parallel per-observer, tick staggering, 118 μs for 10 players × 100 NPCs
- **Entity sync**: automatic [Synchronized] delta detection at 249 ns/entity
- **~6,600 CCU at 30Hz** on a single server under full simulation load

## Status

This package is in **beta** (v0.9.0). The core API is stabilizing and actively used in production game development. Breaking changes are possible but increasingly rare. We encourage you to build with it and share feedback.

## Documentation

📖 [Project Documentation](https://altruist-docs.vercel.app) — Installation, guides, API reference, and benchmarks.

## Links

📨 [Open a GitHub Issue](https://github.com/Vulcaine/Altruist/issues) — Report bugs or request features.

♥️ [Sponsor the project](https://github.com/sponsors/Vulcaine) — Help fund development.

Copyright (c) Aron Gere 2025
