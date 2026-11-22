# Altruist Framework

Altruist is a high-performance, config-driven server framework for building scalable game servers and real-time applications in C# / .NET 9. While it serves as a general-purpose application server (DI, REST APIs, WebSocket/TCP/UDP, auth, persistence), its standout feature is a complete **game simulation engine** — built-in AI, combat, collision detection, visibility tracking, and automatic entity synchronization — all benchmarked with BenchmarkDotNet.

## Highlights

Measured with `Benchmarks/run.sh` (BenchmarkDotNet, .NET 9) on an Apple M1 Pro laptop, 2026-10-03 — see [Benchmarks/BENCHMARK_RESULTS.md](https://github.com/Vulcaine/Altruist/blob/develop/Benchmarks/BENCHMARK_RESULTS.md) for the method, raw data and the caveat that the machine was not idle (larger numbers are noisy):

- **~1.8 ms per full simulated tick** for 50 players + 500 NPCs (movement, AI, delta sync, visibility, collision, combat) — about 5% of a 30 Hz tick budget
- **AI FSM**: 16 ns/entity per update, zero allocations
- **Combat**: ~90 ns single attack (damage calc + apply + hit event), AoE sweep queries (sphere/cone/line)
- **Collision**: SpatialHashGrid broadphase, ~55–85 µs per tick for 100 entities
- **Visibility**: parallel per-observer with tick staggering, ~4 µs steady-state tick for 10 players × 100 NPCs
- **Entity sync**: automatic `[Synchronized]` delta detection, ~0.6–0.8 µs per entity
- Reproduce on your own hardware: `cd Benchmarks && ./run.sh` (or `./run.sh --quick`)

## Status

This package is in **beta** (v0.9.0). The core API is stabilizing and actively experimented with in production game development. Breaking changes are possible but increasingly rare. We encourage you to build with it and share feedback.

## Documentation

📖 [Project Documentation](https://altruist-docs.vercel.app) — Installation, guides, API reference, and benchmarks.

## Links

📨 [Open a GitHub Issue](https://github.com/Vulcaine/Altruist/issues) — Report bugs or request features.

♥️ [Sponsor the project](https://github.com/sponsors/Vulcaine) — Help fund development.

Copyright (c) Aron Gere 2025
