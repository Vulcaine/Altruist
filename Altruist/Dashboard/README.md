# Altruist Dashboard

A dashboard for a running Altruist server, in the browser and in VS Code (the same app). It shows and operates:

- **Overview and configuration**: engine settings, every registered type, every `altruist` key; live keys can be changed.
- **Server and fleet**: capacity and drain (and the drain action), the fleet's servers and the units they hold, the
  process's CPU, memory, GC and threads, ticket and rate-limit settings.
- **Engine**: frame time against the target rate, the world steppers, every `[Cycle]` task and cron job with its runs
  and failures.
- **Worlds**: 2D and 3D worlds followed live, with colliders, terrain and the game's gizmos, and an object inspector.
- **Rooms**: every room host's rooms, seats, players and input buffers, matchmaking queues and lobbies; close a room.
- **AI and zones, pairing, autosave**: agents and their states, zones, phone pairing sessions, autosave services
  (flush now).
- **Data and traffic**: connections, cache entries (edit, delete), vaults (rows, schema, SQL), recorded HTTP and packet
  traffic, latency percentiles, and an API lab that calls any endpoint or gate.

The navigation shows only what the server serves: an area appears when its module is loaded (rooms need
`Altruist.Gaming.Rooms`, autosave `Altruist.Gaming.Autosave`, and so on).

It is a development and operations tool. Its APIs run SQL, edit caches, drain the server and act as any client, so
dashboard access is admin access to the server.

## Enabling it

Reference the `Altruist.Dashboard` package and turn it on in `config.yml`:

```yaml
altruist:
  dashboard:
    enabled: true
    token: ${DASHBOARD_TOKEN}   # at least 16 characters; or:
    # policy: ops               # an ASP.NET Core authorization policy you registered
```

Then open `http://localhost:<port>/altruist/dashboard?dashboard_token=<token>` once; the token is exchanged for a
session cookie. In VS Code, install the Altruist Dashboard extension and run `Altruist: Set Dashboard Token`.

Without `token` or `policy` the dashboard is closed: in the `Development` environment it answers loopback requests
only, in any other environment every dashboard request gets `503`. With `enabled` missing or `false` nothing of it is
mapped.

## APIs

Everything the app shows comes from JSON APIs under `/dashboard/v1` (the same token protects them), for example
`/server`, `/engine`, `/worlds`, `/worlds/{index}/live` (NDJSON: the whole world, then its changes), `/rooms`,
`/gameplay`, `/pairing`, `/autosave`, `/sessions`, `/cache`, `/vaults`, `/network/events`, `/performance` and `/lab`.
`GET /dashboard/v1/summary` lists the areas the server serves in `features`.

## Network recording

`altruist:dashboard:network:*` controls the traffic recorder: `enabled`, `captureHttp`, `capturePackets`,
`retentionMinutes`, `maxEvents`, `maxPayloadBytes`, `slowHttpMs`, `slowGateMs`, `redactFields` (default `password`,
`token`, `authorization`) and `recordNonJsonPayloads` (default `false`). JSON payloads are recorded with the
`redactFields` replaced; a payload that is not JSON cannot be redacted, so only its size is recorded unless
`recordNonJsonPayloads` is `true`.

## Developing the app

The app lives in `App/` (Preact, TypeScript, Vite; three.js for the world viewer). `npm run dev` serves it against a
running server (`ALTRUIST_SERVER=http://localhost:8000`, default 8080), `npm test` runs its unit tests, and
`npm run build` writes `App/dist`, which is committed and embedded into the package (packing needs no Node).

## Documentation

[Altruist documentation](https://altruist-docs.vercel.app): the dashboard page covers every screen and setting.

Copyright (c) Aron Gere 2025
