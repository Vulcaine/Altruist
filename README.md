# 🧙 Welcome, Traveler!

> Feel free to try it — still in progress, expect occasional API changes as the framework evolves. Feedback welcome.

[📖 Read The Docs](https://altruist-docs.vercel.app)

Altruist is a modular, high-performance game server framework for real-time multiplayer games — movement, physics, caching, persistence, visibility, AI, combat, and more, all pluggable.

## Highlights

Measured with `Benchmarks/run.sh` (BenchmarkDotNet, .NET 9) on an Apple M1 Pro laptop, 2026-10-03 — see [Benchmarks/BENCHMARK_RESULTS.md](Benchmarks/BENCHMARK_RESULTS.md) for the method, raw data and the caveat that the machine was not idle (larger numbers are noisy):

- **~1.8 ms per full simulated tick** for 50 players + 500 NPCs (movement, AI, delta sync, visibility, collision, combat) — about 5% of a 30 Hz tick budget
- **AI FSM**: 16 ns/entity per update, zero allocations
- **Combat**: ~90 ns single attack (damage calc + apply + hit event), AoE sweep queries (sphere/cone/line)
- **Collision**: SpatialHashGrid broadphase, ~55–85 µs per tick for 100 entities
- **Visibility**: parallel per-observer with tick staggering, ~4 µs steady-state tick for 10 players × 100 NPCs
- **Entity sync**: automatic `[Synchronized]` delta detection, ~0.6–0.8 µs per entity
- Reproduce on your own hardware: `cd Benchmarks && ./run.sh` (or `./run.sh --quick`)

## Quick Start

Pick your adventure from the [Examples](Examples) folder:

- 🎯 [ShooterGame2D](Examples/2D/ShooterGame2D) — top-down 2D shooter
- ⚔️ [CombatArena3D](Examples/3D/CombatArena3D) — 3D combat arena
- 🔀 [DualTransport.TcpUdp](Examples/3D/DualTransport.TcpUdp) — TCP + UDP side-by-side
- 🌐 [HttpRestApi](Examples/HttpRestApi) — REST endpoints on top of Altruist
- 🧵 [TCP](Examples/TCP) &nbsp;·&nbsp; [WebSocket](Examples/WebSocket) — minimal transport demos

![Demo of Starship](Assets/Videos/starship.gif)

