# Changelog

All notable changes to the Altruist framework are documented in this file.

## [0.9.4-beta] - 2026-04-09

### Changed
- **DI improvements** — `DependencyResolver`, `DependencyPlanner`, `AppConfigLoader`, and `AssemblyLoader` refined for standalone usage.
- **DI.Tests** — 13 new dedicated tests for the standalone DI module (`DependencyResolverTests`).

### Fixed
- Exclude nested `DI.Tests/` project from main `Tests.csproj` to prevent duplicate assembly attribute compilation errors.

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
