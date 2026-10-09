# Altruist Dashboard

A browser dashboard for a running Altruist server: configuration summary, live sessions and rooms, cache entries,
vault (database) rows and SQL, recorded network traffic and latency, and an API lab that calls your portals and HTTP
endpoints. The UI is served at `/altruist/dashboard`, its JSON APIs under `/dashboard/v1`, its live socket at
`/ws/dashboard`.

It is a development and operations tool. Its APIs run SQL, edit caches and act as any client, so dashboard access is
admin access to the server.

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
session cookie.

Without `token` or `policy` the dashboard is closed: in the `Development` environment it answers loopback requests
only, in any other environment every dashboard request gets `503`. With `enabled` missing or `false` nothing of it is
mapped.

## Network recording

`altruist:dashboard:network:*` controls the traffic recorder: `enabled`, `captureHttp`, `capturePackets`,
`retentionMinutes`, `maxEvents`, `maxPayloadBytes`, `slowHttpMs`, `slowGateMs`, `redactFields` (default `password`,
`token`, `authorization`) and `recordNonJsonPayloads` (default `false`). JSON payloads are recorded with the
`redactFields` replaced; a payload that is not JSON cannot be redacted, so only its size is recorded unless
`recordNonJsonPayloads` is `true`.

## Documentation

[Altruist documentation](https://altruist-docs.vercel.app): the dashboard page covers every screen and setting.

Copyright (c) Aron Gere 2025
