# Changelog

All notable changes to the Altruist framework are documented in this file.

## [0.9.5-beta] - 2026-04-10

### Added
- **Column rename migrations** — `[VaultRenamedFrom("old_name")]` attribute. Emits `ALTER TABLE RENAME COLUMN` (metadata-only, instant) instead of destructive drop+add. Supports multiple renames, stale attribute detection.
- **Column type change migrations** — auto-detected by comparing DB store type vs C# mapped type. Same-family widening (int→bigint) uses direct `ALTER COLUMN TYPE`. Cross-type conversions use batched copy (50K rows/batch) to avoid long locks on large tables.
- **Transaction-safe migrations** — all migration operations wrapped in `BEGIN`/`COMMIT`. On failure: `ROLLBACK` + `MigrationException` with operation index and details. Postgres supports transactional DDL.
- **`MigrationException`** — typed exception with operation context for clean error reporting.
- **`IsSameTypeFamily`** — detects safe widening conversions (int/float/text/timestamp/numeric families) to skip unnecessary batched copy.
- **57 migration planner unit tests** — new table creation, type mapping (14 primitives + 7 arrays + nullable/enum/collection), column diff, unique constraints, indexes, FK diff, history tables, schema handling, constraint naming, renames, type changes.
- **`InternalsVisibleTo` for Tests** — Core.csproj exposes internals to test project.

### Changed
- **Migration executor** — `ApplyAsync` now transactional. New abstract methods `ApplyRenameColumnAsync`, `ApplyAlterColumnTypeAsync`.
- **Migration planner** — `PlanExistingTableDiff` processes renames before add/drop, checks type diffs for all existing columns.

### Removed
- **ScyllaDB test files** — deleted deprecated `ScyllaTests.cs`, removed ScyllaDB project reference from Tests.

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
