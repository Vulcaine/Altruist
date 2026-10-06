# Changelog

All notable changes to the Altruist framework are documented in this file.

## [Unreleased]

A complete standalone 2D physics world API (enough to run an authoritative match simulation on it), AI agents that live outside the world organizer, match hosting (rooms, matchmaking, lobbies) as three new packages, a 2D rotation helper, and the server as a fleet member: capacity, drain, multi-core room steps, metrics, and a fleet of servers that finds each other through a shared backplane (one configuration for one server or many). Existing code keeps compiling: the new interface members have defaults and the state machine accepts every existing context. Nothing changes at run time until configured: one step worker, unlimited capacity, and a drain only when the host stops (it finishes at once when no room host is bound).

### Added
- **Standalone 2D worlds** — `PhysxWorldEngine2D.Create(PhysxWorldSettings2D)` (gravity, fixed dt, velocity / position iterations) and `IPhysxWorldEngineFactory2D.Create(settings)`. `IPhysxWorldEngine2D` gains `CreateBody(PhysxBodyDef2D)` (type, position, angle, bullet, sleep, fixed rotation, damping, gravity scale, user data), `CreateFixture(body, PhysxFixtureDef2D)` (circle, box, convex polygon, chain / loop, edge shapes from `PhysxShape2D`; density, friction, restitution, `PhysxFilter2D` category / mask / group, trigger, user data), `SetContactListener(IPhysxContactListener2D)` (begin, end, pre-solve with `IsEnabled` / `Restitution` / `Friction` overrides and `GetWorldManifold()`, post-solve impulse), `RayCast(from, to, IPhysxRayCastCallback2D)` (ignore / clip / stop per hit) and `Contacts` (the world's current contacts). Fixtures are `IPhysxFixture2D` and raise the collider enter / exit and trigger events.
- **Rigid-body members on `IPhysxBody2D`** — `UserData`, `IsAwake`, `IsEnabled`, `WorldCenter`, `SetTransform(position, angle)`, `GetWorldVector` / `GetLocalVector` / `GetWorldPoint` / `GetLocalPoint`. Box2D bodies implement them natively; other implementations get defaults.
- **`Rotation2D.Rotate(v)` / `Unrotate(v)`** — rotate a vector by the angle (local to world) and back.
- **`Altruist.Gaming.Rooms`** (new package) — `RoomHost<TSim, TInput, TPlayer>` hosts fixed-step, authoritative rooms as an `IWorldStepper`: one connection per principal (the newest replaces the old), rooms whose seats are driven by players or bots (`ISeatController`), sequenced inputs (`InputBuffer`: stale / duplicate drops, overflow cap, starved steps repeat the last input), snapshots on a global step phase (shared capture, per-receiver ack and input depth), reconnect grace and rejoin, bot refills of open seats, team forfeits, the room's end and disposal. Lifecycle per room with `RoomRules` (`DisconnectAction`, `GraceExpiredAction`, `VoluntaryLeaveAction`, `EmptyRoomAction`, team-seat reclaim, join in progress, bot refill). The game plugs in its simulation (`IRoomSimulation<TInput>`), seat bookkeeping, results and packets through `IRoomGame`; packets go out through `IRoomTransport`. Features attach as `IRoomHostModule`s with hooks at fixed points of the host's flow.
- **`Altruist.Gaming.Matchmaking`** (new package) — `MatchmakingModule`: queues per playlist (`PlaylistOptions`: mode, team size, rated, room rules), fill groups (a full group at once, otherwise whoever waits after `FillAfterSeconds`, bots fill), rated groups inside widening rating windows, team balancing by rating sum, join in progress (open seats at once, bot seats one at a time with a notice and pacing, reserved players do not start new rooms), queue status (`IMatchmakingGame.QueueStatus`). `Matchmaker` is the pure queue / grouping core.
- **`Altruist.Gaming.Lobbies`** (new package) — `LobbyModule`: lobbies by invite code, host migration, start a room with the members (alternating teams, bots fill to the mode's team size), return after the room (the host ends it for everyone), rejoin a lobby room after a reconnect (`ILobbyGame` supplies modes and packets).
- **Server node (`IServerNode`, default `ServerNode`)** — this process as a member of a fleet. **Capacity**: every `ICapacityContributor` (a service, or `Register(...)` at run time) reports units and load per kind; `Capacity()` sums them against `altruist:server:capacity:max-load` (0 = unlimited) into a `ServerCapacity` report with a state (`Starting` until `IServerStatus` is alive, `Ready`, `Full`, `Draining`, `Drained`); `CanAccept(cost)` admits new work. **Drain**: `DrainAsync(timeout)` stops new work, tells every `IDrainParticipant` once (also ones that register during the drain), waits for them up to `drain:timeout` (30 s), then force-stops what still runs and waits `drain:force-grace` (5 s); every caller shares one drain. Nothing here runs on the engine's hot path or touches the network.
- **Drain on shutdown** — `ServerDrainOnShutdown` (an `IHostedLifecycleService`) drains the node in `StoppingAsync`, before any hosted service stops, so SIGTERM / a Kubernetes pod termination lets running rooms finish. The host's shutdown timeout is raised to cover the drain. Config `altruist:server:drain:on-shutdown` (true).
- **Server endpoints** — `GET /altruist/server/live` (liveness), `GET /altruist/server/ready` (200 only while `Ready`: a full or draining server leaves the load balancer), `GET /altruist/server/capacity` (the report, for allocators and dashboards), `POST /altruist/server/drain?wait=&timeoutSeconds=` (a preStop hook or an operator; loopback only, or the `X-Altruist-Drain-Token` header when `altruist:server:drain:token` is set).
- **Metrics** — meter `Altruist.Server` (`altruist.server.load`, `.max_load`, `.state`, `.accepting`, `.units` and `.kind_load` tagged `kind`) and meter `Altruist.Engine` (`altruist.engine.frame.duration`, `.stepper.duration` tagged `stepper`, `.fixed_steps`, `.overruns`). Standard `System.Diagnostics.Metrics`: export them with OpenTelemetry / Prometheus / `dotnet-counters`.
- **Multi-core steps (`IStepScheduler`, default `StepScheduler`)** — runs the independent units of one step on several threads; every unit runs even when others throw (the failures are thrown together afterwards). Config `altruist:game:engine:step-workers`: `1` (default), a number, or `auto`.
- **Rooms on a fleet** — `RoomHost.UseScheduler(scheduler)` steps the rooms' simulations in parallel (ends, snapshots and every packet stay on the engine thread, in room order: bit-identical to one worker). `RoomHost.UseServer(node)` counts the rooms against the server's capacity (kind `rooms`, `IRoomGame.LoadOf(mode, playlist)`, default 1) and joins its drain: matchmaking and lobbies open rooms only while `CanOpenRoom` (a full server keeps players queued and still backfills running rooms); a drain empties the queues, calls off pending joins, refuses new queues and lobbies (`LobbyRejectReason.Unavailable`), lets running rooms end (rejoins still work), tells connections without a room (`IRoomGame.ServerDraining()`), and on a timed-out drain closes the rest (`IRoomGame.OnForcedClose`). New module hook `IRoomHostModule.OnDraining`; `MatchmakingModule.Enqueue` returns whether it queued.
- **Fleet (`IFleet`, default `Fleet`)** — the servers of one game see each other: every server publishes itself (internal and public address, region, state, capacity, module stats) with a heartbeat (`altruist:server:fleet:heartbeat`, 2 s; gone after three missed beats), claims the units it runs (`Claim` / `TryClaimAsync` / `LocateAsync`; a crashed server's claims point nowhere) and hands work to a player moving to another server (`PutHandoffAsync` / `TakeHandoffAsync`, bound to the target server). Config `altruist:server:fleet`: `cluster`, `region`, `internal-address` (default: this machine's address and the HTTP port; `{ip}` placeholder), `public-address` (`{node-id}` placeholder; without one, clients go through the relay), `node-param` (`node`), `secret` (relay signature; default the security key). `FleetHeartbeatService` beats from start-up and leaves the fleet after the drain.
- **Fleet backplanes (`IFleetBackplane`)** — `InMemoryFleetBackplane` (default: a fleet of one, every decision stays local) and `RedisFleetBackplane` in `Altruist.Redis` (config `altruist:server:fleet:redis`, Redis 6.2+). A server that cannot reach the backplane keeps serving alone and rejoins when it can. One configuration works for one server and for many: servers that share the backplane are used as soon as they appear.
- **Node relay (`FleetRelayMiddleware`)** — a WebSocket request with `?node=<id>` for another live server is relayed to that server's internal address (frames both ways, close handshake passed on), so a fleet needs one public address and no sticky routing; this server, an unknown one or an unreachable one serve the request here. The relayed request carries the client's address, scheme and host in HMAC-signed headers, which the receiving server restores (`RemoteIpAddress`); unsigned, stale or tampered relay headers are stripped and ignored.
- **Rooms in a fleet** — `RoomHost.UseFleet(fleet)`: players' rooms are claimed (`player`), a connection without a room here looks up its room elsewhere and handed-over work (off the engine thread; a rejoin request waits for the answer) and is sent on with `IRoomGame.Redirect(RoomRedirect)` (null = nobody moves); `RoomHost.Redirect(session, node, reason, handoff)` writes the handoff before the client is told, then closes the connection. `RoomHost.Post` / `RunAsync` run I/O off the engine thread and continue on it. New module hook `IRoomHostModule.OnHandoff`. A draining server sends newcomers to another server.
- **Matchmaking in a fleet** — each server publishes its queue lengths (`queue:<playlist>`); a playlist's players gather on the server with room and the most of them waiting (ties: the lowest server id, so every server agrees), keeping their waiting time; a full server sends its queue to one with room; a draining server hands its queue over (else tells the players they are idle). `MatchmakingOptions.FleetMoveCooldownSeconds` (3).
- **Lobbies in a fleet** — lobby codes are claimed (`lobby`); joining a code on another server leads to the lobby's server; a draining server moves its lobbies (code, members, host) to another server once they are not playing a room, and sends lobby requests elsewhere. `Lobby.MakeHost`.
- **Worlds in the capacity report** — `WorldCapacityContributor` reports the 2D/3D organizers' worlds (kind `worlds`, load `altruist:server:capacity:world-load` each, default 0).
- **Parallel Box2D worlds** — `Box2DThreading` makes Box2DSharp's static contact pools per-thread (installed by the first world), so independent 2D worlds may step on different threads at once and move between threads between steps; `PhysxWorldEngine2D.ParallelWorldsSupported`. On one thread the pooling is unchanged (bit-identical).
- **AI agents outside the world** — `IStateContextCore` is the state machine context without a world entity (`IStateContext` adds `Entity`), and `IAIContext` derives from it, so a bot in a match room or a headless simulation can run an AI behavior. `AIContext` is a base class with the bookkeeping members. `AIBehaviorDiscovery.CreateStateMachine<TBehavior>()` builds (once) and returns a machine for a behavior type without an assembly scan, for agents their owner ticks itself.

### Changed
- `Body2DAdapter.Mass` setter scales mass and rotational inertia to the given mass (it was ignored).
- `Box2DWorldEngine2D.Step` uses the world's configured iterations (default 8 / 3, as before).
- `AIBehaviorDiscovery` templates are thread-safe.
- The "server is going away" packet (`IRoomGame.ServerDraining`) goes to a connection at most once.
- **Postgres bootstrap is serialized across servers**: schema creation, migrations and initializers run under a Postgres advisory lock, so servers of a fleet starting together against one database no longer crash on concurrent `CREATE SCHEMA` (the others wait and find the schema migrated).
- `IAltruistContext.ProcessId` no longer contains a stray `$` (`host-pid-guid`); it is the server node's id.
- **2D physics defaults and events** — `default(PhysxFilter2D)` / `new PhysxFilter2D()` equal `PhysxFilter2D.Default` (category 1, mask 0xFFFF) and `default(PhysxBodyDef2D)` means `AllowSleep = true`, `GravityScale = 1` (stored relative to the defaults; the public members are unchanged). `RayCast(PhysxRay2D, maxHits)` returns the closest `maxHits` hits sorted by fraction. Fixtures raise `OnCollisionStay` after every later step a solid contact touches, and `OnCollisionEnter` is raised after its step with the normal impulse the solver applied (stays carry theirs). `PhysxBodyType.Static` stays the default body type (as in Box2D), now documented.
- **Contacts you can keep** — `IPhysxContact2D.ToInfo()` copies a contact into a `PhysxContactInfo2D`. `Contacts` yields a distinct view per contact (safe to collect), valid until the contact list changes (next step, a body removed or disabled); a stale view, or a listener contact used after its callback, throws instead of reading another contact. Listener callbacks still reuse one view (no allocation).
- **Opt-in fixed stepping** — `PhysxWorldSettings2D.MaxSubSteps` (default 0: `Step(dt)` steps by `dt` exactly, as before). Above zero, `Step(dt)` accumulates and runs whole steps of `FixedDeltaTime`, at most `MaxSubSteps` per call.
- **2D world factory everywhere** — `WorldEngineFactory2D` (`IPhysxWorldEngineFactory2D`) is registered in every environment mode, and the default `Create(settings)` applies every setting through the new `IPhysxWorldEngine2D.ApplySettings` (it dropped the iterations).
- `AIBehaviorDiscovery.CreateStateMachine<TBehavior>()` caches templates per type: a behavior whose `[AIBehavior]` name is held by another type runs its own handlers (the name keeps its first registration).
- **Room rules keep their promises**: `OnGraceExpired` also applies with `BotAfterDelay` (`RemoveSeat` / `Abandon` take the seat the player's bot drives; a seat someone else took is left alone); `Hold` + `ForfeitRejoin` hands the held seat to a bot instead of leaving it idle; `RoomRules.BotRefill` defaults to `true` (seats the rules open get their bot; `false` keeps them open for join in progress); a `TeamForfeit` room reports `OnTeamForfeit` once and ends in a draw when only bots are left.
- A room whose step throws no longer cuts a single-worker step short: every room steps, the snapshots go out and the failures are thrown together, as with several workers.
- `RoomHostOptions.StepAfterEnd` (default `true`, as before): `false` stops stepping ended rooms until they are released.
- `RoomHost.TryStart(room)` starts a room only while the server takes it; matchmaking (the group waits on) and lobbies (`Unavailable`) use it. `Start` stays unconditional.
- `RoomHost.TakeOver(session, module)` and `IRoomHostModule.OnTakenOver`: `MatchmakingModule.Enqueue` refuses a connection in a room or joining one and takes it out of its lobby; `LobbyModule.Join` ignores a connection in a room, takes it out of the queue and out of a previous lobby.
- Matchmaking: open seats are taken at once (only bot seats wait `SwapNoticeSeconds`); rated rooms with `JoinInProgress` take queued players whose rating window covers the room's mean rating.
- Lobbies: a member who drops during the lobby's room keeps its place and the host role (`LobbyMember.Connected`) until it can no longer rejoin, so a lobby room survives everyone dropping at once; the host role moves to the next connected member; `Start` refuses a mode the members do not fit (`LobbyRejectReason.TooManyPlayers`; `TeamSizeOf` 0 = no fixed teams).

### Notes
- One 2D world is stepped by one thread at a time; different worlds may step in parallel (`Box2DThreading`).
- With more than one step worker, a room's `IRoomSimulation.Step`, its bots' `Think` and `IRoomGame.BeforeStep` may only touch that room.

### Tests
- `PhysxWorld2DSimulationTests` (also zero-value filter / body def, fixed stepping, sorted ray casts, contact views and copies, the factory fallback, enter impulse and stay events), `AIAgentTests` (also two behaviors sharing a name), `RoomHostTests` (a small line-walking game through matchmaking, join in progress, reconnects, rated abandons and lobbies).
- `RoomRulesTests` (every grace-expiry combination, bots for opened seats, team forfeits, open-seat and rated join in progress, lobby rooms surviving disconnects, lobby team sizes, the connection's state checks, `TryStart`, ended rooms); the failing-room test runs with one and four workers.
- `ServerNodeTests` (capacity, states, drain, timeouts, force stop, faulty participants, concurrency, metrics), `ServerNodeDiTests` (config values through Altruist DI), `ServerNodeEndpointTests` (the endpoints and the shutdown drain on a real Kestrel host), `StepSchedulerTests` and `WorldCoordinatorMetricsTests`, `Box2DThreadingTests` (parallel worlds bit-identical to serial ones; they fail without the per-thread pools), `RoomFleetTests` (parallel rooms bit-identical with identical packets, failing rooms, admission against capacity, drain flows, forced close, a live engine thread).
- `FleetTests` (registry, heartbeats, expiry, claims, take-over, handoffs, clusters, a broken backplane, config), the backplane contract against the in-memory and a real Redis backplane (`ALTRUIST_TEST_REDIS`), `FleetRelayTests` (two real servers: relayed WebSockets, messages and closes both ways, fallbacks, signed and forged relay headers), `FleetRoomTests` (several servers in one process with simulated clients that follow redirects: gathering queues, overflow, rejoin from any server, lobby codes anywhere, drain hand-over of queues and lobbies, crashed servers). CPU-heavy tests run in their own collection (`cpu-heavy`).

## [0.9.9-beta] - 2026-10-04

Per-client outbound queues with packet coalescing, and a config-driven rate-limit interceptor. Existing apps need no changes: `outbound:mode` defaults to `direct` (the previous behaviour) and the rate limit is only installed with `rate-limit:enabled: true`.

### Added
- **Outbound queues** — `ClientSender.Enqueue(clientId, packet)` queues a packet and returns at once; one pump per client encodes and sends off the caller, in order, into a reused buffer (`IBufferEncoder`). `CloseAfterFlush(clientId)` closes the connection after everything queued before it; `Forget(clientId)` drops a client's queue (the connection manager calls it when a connection is gone). A client whose send is stuck longer than `stuck-send-seconds` or that has more than `max-queued-per-client` packets waiting is aborted (normal disconnect path). Every sender resolved from DI shares the process-wide `OutboundQueues` service.
- **`[Coalesce("key")]`** — a client's queue keeps at most one unsent packet per key (the newest, moved behind everything queued before it), e.g. for world snapshots.
- **`altruist:server:transport:outbound:mode: direct | queued`** — `queued` makes `ClientSender.SendAsync` (and the room and broadcast senders built on it) enqueue and return; `direct` (default) encodes and awaits the socket as before. Config: `outbound:{mode,max-queued-per-client,stuck-send-seconds,encode-buffer-bytes}` (direct / 256 / 5 / 2048).
- **`IOutboundMetrics`** — register one in DI to receive queue depth, coalesced packets, aborted slow clients, sent bytes and send errors.
- **`RateLimitInterceptor`** — per-connection token buckets from `altruist:server:transport:rate-limit` (`enabled`, `max-payload-bytes`, `oversize-strikes`, `strikes-to-disconnect`, `strike-window-seconds`, `default-bucket`, `routes`, `buckets: { name: { capacity, per-second, gates } }`). A gate's bucket comes from config, else from **`[RateLimit("bucket")]`** on the gate handler, else the default bucket. Over-limit and oversize packets are rejected and counted as strikes; too many strikes in the window close the connection. Applies to unknown events too. Replace the token buckets by registering an `IRateLimiter`; receive verdicts with **`IRateLimitMetrics`**. `TokenBucketRateLimiter` and `RateLimitOptions.FromConfiguration` are public.
- **`IConnectionStateInterceptor`** — an interceptor with per-connection state gets `Forget(clientId)` from the connection manager once the connection is gone.
- **`altruist:game:worlds:entity-sync-hz`** — the rate the 2D/3D organizers pass to the entity sync service for `[Synchronized]` Hz/Seconds frequencies (default 25, unchanged).

### Changed
- **Worker mode keeps the threading of 0.9.7 and earlier.** 0.9.8 ran every engine frame on the engine thread in both world-step modes; this now applies to `world-step: inline` only. With `worker` (the default) a frame continues on a thread-pool thread after the timer wait and async next-tick delegates are awaited asynchronously, as before 0.9.8.
- `ClientSender` and `EngineClientSender` have an additional constructor taking the shared `OutboundQueues` (used by DI); the existing constructors are unchanged.

### Tests
- `OutboundQueueTests`, `RateLimitTests`, `EngineCompatibilityTests` (worker-mode threading regression, organizer sync rate).

## [0.9.8-beta] - 2026-10-04

Fixed-step world stepping, inline world step, one-shot timers and config-driven cycle rates. Existing apps need no changes: every new behaviour is behind a config key whose default keeps the 0.9.7 behaviour, except the plain-`[Cycle]` rate fix below.

### Added
- **`WorldCoordinator`** — the new default `IGameWorldOrganizer`: a composite that steps every `IWorldStepper` registered in DI, in registration order. Variable-rate steppers (`StepMode.Variable`) get `Step(dt)` with the real frame time; fixed-rate steppers (`StepMode.Fixed`, `FixedHz`) get `FixedStep(step)` 0..N times per frame with a constant `dt`, one fixed-step clock per stepper. Every stepper also gets `BeforeSteps` / `AfterSteps(FrameInfo)` once per frame. A stepper that throws is logged and does not stop the others. Steppers are resolved on the first step, so a stepper may depend on `IAltruistEngine`.
- **`FixedStepClock`** — double-precision accumulator with a frame-time clamp and a step cap. Config: `altruist:game:engine:fixed-step:max-frame-delta` (0.25 s), `max-steps-per-frame` (8), `overrun` (`drop` | `carry` | `slow-motion`, default `drop`).
- **`altruist:game:engine:world-step: inline`** — the world step runs on the engine thread as the last phase of every frame (next-tick queue → `[Cycle]` tasks → dynamic tasks → effects/timers → world step), so commands posted with `WaitForNextTick`, cycles and steppers share one thread and a fixed order. `worker` (the default) keeps the separate world-worker task.
- **One-shot timers** — `IAltruistEngine.ScheduleOnce(TimeSpan, Action)` and `ScheduleAtFrame(long, Action)`, cancellable with `CancelEffect`; `IAltruistEngine.Frame`.
- **`RunOffTick`** — runs I/O on the thread pool and hands the result (or exception) back on the engine loop.
- **`[Cycle(Config = "key", Default = n, Unit = ...)]`** — cycle rate read from configuration at registration.
- **`IEngineClock`**, **`ManualEngineClock`** and **`EngineTestDriver`** — run engine frames by hand, deterministically, on an engine that was never started (tests).
- **`[ConditionalOnMissingService(typeof(T))]`** — DI: register a framework default only while no other class (or manual registration) provides `T`. The `WorldCoordinator` uses it, so an app's own `IGameWorldOrganizer` still replaces it.

### Changed
- **The engine frame runs on the engine thread.** Frames used to resume on thread-pool threads after the timer wait; every synchronous part of a frame now runs on `EngineThread` (async delegates continue where their `await` resumes, as before).
- The 2D/3D world organizers are registered as `IWorldStepper` (variable rate) instead of `IGameWorldOrganizer`; they are stepped by the `WorldCoordinator` with the same frame time as before. `IGameWorldOrganizer2D` / `IGameWorldOrganizer3D` are unchanged.
- **`ForceRuntime2D` / `ForceRuntime3D`** step as fixed-rate steppers at 25 Hz (dt 0.04 s) instead of a `[Cycle]` that assumed 25 Hz; `TickFrame()` is still available.
- Effects expire on the engine clock (the UTC deadline is converted when the effect is scheduled) and run in scheduling order.
- Time-based cycle and effect rates (`Hz`, `Seconds`, `Milliseconds`) keep their average rate instead of drifting to the next frame boundary (a 30 Hz cycle on a 120 Hz engine ran every 5th frame, 24 times a second); after a stall they resume without a burst.

### Fixed
- **Plain `[Cycle]`** (and `ScheduleTask` without a rate) runs every engine frame. With `unit: ticks` (the default) it ran once every `framerateHz` frames, i.e. once per second (the known issue of 0.9.7). If you relied on that, use `[Cycle(1, CycleUnit.Hz)]`.

### Tests
- `FixedStepClockTests`, `WorldCoordinatorTests`, `EngineFramePipelineTests` (inline order and thread, cycle rates, timers, `RunOffTick`, engine clock), `ForceRuntimeFixedStepTests`, `ConditionalOnMissingServiceTests`.

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
