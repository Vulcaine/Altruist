# Changelog

All notable changes to the Altruist framework are documented in this file.

## [0.9.7-beta] - 2026-10-04

Engine lifecycle and DI fixes. No API was removed and no configuration key changed; existing apps need no changes.

### Fixed
- **One construction per boot** — `EngineStartupConfiguration` built a throwaway service provider during configuration, which constructed every singleton a second time and started a second engine on it (an orphan `EngineThread` that waited forever). Configuration now only registers. `[Cycle]` methods are registered once by `MethodScheduler`'s `[PostConstruct]`, on the instances whose `[PostConstruct]` ran; the visibility tracker and the 2D/3D organizer wire each other in the tracker's `[PostConstruct]`; the engine is started only by `ServerStatus` once every connectable service is up.
- **Duplicate singletons from the dependency planner** — a `[Service]` that the planner had already registered as the dependency of a service scanned earlier got a second descriptor from its own attribute (two instances per boot, listed twice in `IEnumerable<T>`). The planned registration is now reused (an interface registration becomes a forward to the implementation).
- **The game loop survives exceptions** — a `WaitForNextTick` / `SyncCommit` delegate that throws no longer stops the engine loop or the delegates queued after it. Failing `[Cycle]` tasks, effects and world steps are logged (repeats summarized every 10 s) instead of being swallowed silently; the loop keeps running.
- **`SyncCommit` with `diagnostics: false`** — `EngineWithoutDiagnostics.SyncCommit` threw `NotImplementedException`; it now runs the commit on the next engine frame like the diagnostics engine does.
- **`CycleUnit.Seconds` / `CycleUnit.Milliseconds`** — `[Cycle(30, CycleUnit.Seconds)]` (and `ScheduleEffect` with such a rate) ran about once every 333,000 s instead of 30 times per second: the engine read the stored period (in `TimeSpan` ticks) as seconds. `CycleRate.Frequency` exposes the configured frequency.
- **World step time** — when the world worker fell behind, the dt of the frames it missed was dropped (the channel kept only the latest frame). The frame dts are now summed until the worker picks them up, so the simulated time matches the real time.
- **Async `[Cycle]` methods** — the returned `Task` was discarded, so a slow async `[Cycle]` overlapped itself. Parameterless `[Cycle]` delegates are now invoked directly (no reflection per frame) and an async one never runs concurrently with itself.
- **Engine restart** — after `ServerStatus` stopped the engine (a connectable service failed) and started it again, the world worker never stepped again, and stopping while the engine waited for the server could crash the engine thread. `ServerStatus` publishes `Alive` before it starts the engine, so the loop no longer idles up to 5 s at startup.
- `[Cycle]` methods registered after the engine started (a `[PostConstruct]` running after `ServerStatus` went alive) are picked up safely on the next frame.

### Added
- **Interceptors from DI** — `ConnectionManager` installs every `IInterceptor` registered in DI (`[Service(typeof(IInterceptor))]`); `AddInterceptor` keeps working and ignores an instance that is already installed. `InterceptContext.Route` carries the portal path of the connection, so an interceptor can target one portal.
- **Regression tests** for all of the above (`EngineLifecycleRegressionTests`, `DiInterceptorRegistrationTests`, `PlannedRegistrationDedupTests`).

### Known issue
- A plain `[Cycle]` (realtime) runs once every `framerateHz` frames, i.e. once per second, when `altruist:game:engine:unit` is `ticks` (the default); with `unit: "hz"` it runs every frame as documented. Use `[Cycle(1)]` for every frame. To be addressed together with config-driven cycle rates.

## [0.9.6-beta] - 2026-10-03

### Fixed
- **MessagePack codec** — a structure guard rejects decode bombs (deeply nested or oversized maps/arrays) before deserialization; a map32 header with a huge count now raises `MessagePackSerializationException` instead of an `OverflowException`. New `IBufferEncoder` encodes into a reused buffer.
- **WebSocket transport** — one reused receive buffer per connection (single-frame messages copied out at their exact size) instead of a 4 KB allocation per message; oversized frames are rejected. Close handshakes are bounded (5 s): a peer that never reads or never answers is aborted instead of holding the connection and its read loop open.
- **Per-packet overhead** — one timeout source per connection re-armed per message; no stopwatches, recorder state machine or interceptor task arrays allocated per inbound packet.
- **Connection store** — `JoinRoomAsync` no longer deletes the waiting room when its last connection moves to another room.
- **Persistence** — ambient transactions (`InTransactionAsync`): nested calls join the outer transaction, parallel awaits inside it are serialized. `[Transactional]` works: the decorator is no longer `sealed` and resolves interface methods to their implementation.
- **Postgres** — query and join translator fixes, `SqlVault` ordering, provider wiring.
- **Migrations** — adding a unique column to an existing table no longer creates two identical UNIQUE constraints; executor ordering fixes.
- **Dashboard** — production build fits its size budget again; the world scene spec and the TestApp compile against the current API.
- **Security, config, 2D game world** — JWT and Shield attribute fixes, config loader / converter fixes, `GameWorldOrganizer2D` / `VisibilityTracker2D` wiring, engine frequency.

### Security
- **MessagePack** 3.1.3 → 3.1.10 in every project that references it.
- Pinned vulnerable transitive packages in Core: **Newtonsoft.Json** 13.0.4, **System.Net.Http** 4.3.4, **System.Text.RegularExpressions** 4.3.1. `dotnet list package --vulnerable` reports none.
- **Dashboard UI** — Angular 21.2.25 and refreshed dependencies (with overrides for vite, piscina and uuid): all Dependabot advisories resolved, including the critical websocket-driver and shell-quote ones. The embedded dashboard build is regenerated.

### Added
- **105 regression tests** (82 unit, 23 Postgres integration) covering the fixes above. Integration tests run against `ALTRUIST_TEST_PG` in a throwaway `altruist_test_*` database and skip when no Postgres is reachable (`ALTRUIST_TEST_PG_REQUIRED=true` makes them fail instead, as CI does).
- **CI** — build and tests also run on `develop`, with a Postgres 16 service; unit and integration tests run as separate steps.
- **Reproducible benchmarks** — `Benchmarks/run.sh` runs the suites, records the machine and writes a summary that recomputes every README figure. `BENCHMARK_RESULTS.md` maps each claim to its benchmark; READMEs gained a Highlights block with the measured numbers.

### Changed
- README figures updated to fresh measurements; the CCU estimate was dropped (it was not derived from a benchmark).

## [0.9.5-beta] - 2026-04-10

### Added
- **`[VaultRenamedFrom]`** — rename a column without losing data. Stackable to preserve full rename history; the migration picks the first match in the current database.
- **`[VaultColumnCopy]`** — copy data from one column to another with automatic type conversion. Batched for large tables (configurable via `altruist:persistence:migration:batch-size`).
- **`[VaultColumnDelete]`** — mark a column for deletion. The property stays in code as self-documenting history. Pair with `[Obsolete]` for IDE warnings/errors on usage.
- **`[VaultTableDelete]`** — mark an entire vault table for deletion. History table is dropped automatically.
- **`[VaultArchived]`** — safer alternative to delete: copies all data to an archive table before dropping the original.
- **Automatic type change detection** — changing a property type (e.g. `int` → `long`) is detected automatically and migrated. Same-family widening is instant; cross-type conversions are batched.
- **Transaction-safe migrations** — all operations run in a single transaction. On failure, everything rolls back cleanly with a descriptive error.
- **Configurable batch size** — `altruist:persistence:migration:batch-size` in config.yml (default 50,000 rows per batch).
- **59 migration planner unit tests** covering all operations, type mappings, renames, type changes, and edge cases.

### Changed
- **Migration execution order** — guaranteed: schema changes → column copies → column deletes → foreign keys. Safe to copy data out of a column and delete it in the same migration.

### Removed
- **ScyllaDB tests** — removed deprecated test files and project reference.

## [0.9.4-beta] - 2026-04-09

### Changed
- **Standalone DI** — improved dependency resolution, circular dependency detection, config loading, and assembly discovery for projects using only `Altruist.DI` without the full framework.
- **Lazy\<T\> dependency injection** — `Lazy<T>` constructor parameters now break circular dependency chains at construction time, enabling bidirectional and multi-way service references without runtime errors.

### Added
- **26 DI-specific tests** — dedicated test suite covering `Lazy<T>` cycle-breaking (2-way, 3-way, 4-way), circular dependency error messages with readable paths, mixed direct and deferred resolution, self-references, diamond dependencies, and deep chains.

## [0.9.3-beta] - 2026-04-08

### Added
- **Altruist.DI** — standalone dependency injection package. Any .NET project can now use Altruist's DI features (`[Service]`, `[AppConfigValue]`, `[ConditionalOnConfig]`, `[PostConstruct]`, keyed services) without pulling in the full server framework. Entry point: `await AltruistDI.Run(args)`.
- **Lag compensation** — module-agnostic server-side lag compensation service (`ILagCompensationService`). Any module can wrap logic in `RewindWorld(tick, callback)` + `Compensate()` for temporal hit validation. Enabled via config `altruist:game:lag-compensation`.
- **Entity system** foundation for structured game entity management.
- **Comprehensive autosave test suite** — 80 tests covering unit, integration, WAL enabled/disabled, mock vault with batch/fallback, concurrency, crash recovery, and attribute configuration.

### Changed
- **DI extracted from Core** — `DependencyResolver`, `DependencyPlanner`, all DI attributes, config loader, and bootstrap moved to standalone `Altruist.DI` project. Core now references DI as a dependency.
- **DependencyPlanner decoupled from VaultAttribute** — service marker registration is now plugin-based via `DependencyPlanner.RegisterServiceMarker(Type)`. Core registers `VaultAttribute` at bootstrap; DI layer has no persistence coupling.
- **AltruistBootstrap.Services** now delegates to `AltruistDI.Services` — single shared service collection.
- **CombatService** updated to use `Compensate()` position transformer instead of removed `GetCompensatedPosition`.
- Removed redundant NuGet package references from Core and Boot (now provided transitively by DI).

### Fixed
- **S2190 (infinite recursion)** — `Connection.Type` setter had `set => Type = value` causing stack overflow. Now a no-op setter (getter returns `GetType().Name`).
- **S2930 (undisposed resources)** — `CancellationTokenSource` and `UdpClient` instances now properly disposed:
  - `GeneralDatabaseProvider._healthCts` disposed on health check stop.
  - `MainEngine._cts` and `_linkedCts` disposed on Stop() and re-created on Start().
  - `UdpTransport._udpClient` disposed via new `IDisposable` implementation.
- **CA2100 (SQL injection)** — suppressed false positives in `GeneralDatabaseProvider` where SQL is framework-generated from schema metadata, not user input.
- **CA5394 (insecure random)** — suppressed false positive in `TaskIdentifier` where `Random.Shared` is used for non-security scheduling jitter.
- **60+ npm vulnerabilities** in Dashboard UI resolved — regenerated `package-lock.json` with current dependency versions, added vite `^7.3.2` override to patch remaining CVEs.

### Security
- 0 vulnerable NuGet packages across all 16 projects.
- 0 deprecated NuGet packages.
- 0 npm vulnerabilities in Dashboard UI.
- Full Roslyn code analysis (`AnalysisLevel=latest-all`) passes with 0 security warnings.
- SonarQube analysis: Security Rating **A**, 0 vulnerabilities, 0 open blocker bugs.

## [0.9.2-beta] - 2026-04-06

### Fixed
- Boot PackageId corrected (`Boot` to `Altruist.Boot`).
- Combat PackageId corrected (`Combat` to `Altruist.Gaming.Combat`).
- Movement marked as non-packable (deprecated).

## [0.9.1-beta] - 2026-04-05

### Changed
- Updated all package READMEs for NuGet listing.
- Fixed CI release order and marked deprecated packages.
- Removed benchmark highlights from package READMEs.

## [0.9.0-beta] - 2026-04-04

### Added
- Initial public beta of the Altruist game simulation framework.
- Core DI with attribute-based auto-registration, config binding, conditional services.
- Game engine with fixed-timestep tick loop, cron scheduling, effect system.
- 3D world management with spatial indexing, visibility tracking, delta sync.
- Combat system with sweep queries (sphere, cone, line), spatial broadphase.
- Physics engine with collision detection, rigidbody simulation.
- Networking with WebSocket, TCP, and dual-transport support.
- Persistence layer (Vault ORM) with Postgres, ScyllaDB, Redis, EF Core providers.
- Autosave system with WAL crash recovery, batch flushing, owner-level saves.
- Movement, inventory, regen modules.
- Dashboard with Angular UI for server monitoring.
- 245 tests, 9 example projects.
