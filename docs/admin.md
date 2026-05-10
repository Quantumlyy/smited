# Admin UI

Single-page Blazor Server smoke-test surface for the daemon. Open
`http://127.0.0.1:7779/` in a browser while the daemon is running.

## Scope

The admin UI is for "did my sensation actually fire on the suit?" — a
browser-based replacement for `grpcurl`. It is **not** a polished admin
product, has **no authentication**, and binds to **localhost only**.

The UI is backend-agnostic: every registered backend gets a card and is
selectable in the sensation tester. This includes `mock-owo` (always on)
and the real OWO Skin backend (`Smited:Backends:EnableOwo = true` on
Windows). Bundled sensation files in `sensations/<kind>/` are loaded with
scope "kind" by default, so they apply to every backend of that kind —
on a Windows host with both the mock and real OWO backends enabled, the
same sensation appears in the dropdown for either.

## Defaults

| Setting                    | Default     |
| -------------------------- | ----------- |
| `Smited:Admin:Enabled`     | `true`      |
| `Smited:Admin:Port`        | `7779`      |
| `Smited:Admin:BindAddress` | `127.0.0.1` |

To run the daemon without the admin UI (headless / production):

```json
{ "Smited": { "Admin": { "Enabled": false } } }
```

When `Enabled = false`, the daemon doesn't start the third Kestrel
listener and the startup banner omits the `Admin` row entirely (it
doesn't show "disabled"; just no row). gRPC and panic continue to work
unchanged.

## Don't bind to `0.0.0.0`

The admin UI ships with no auth in v1. Anyone who can reach
`http://<host>:7779/` can fire arbitrary sensations and trigger panic. Keep
`BindAddress = 127.0.0.1` until the v2 shared-secret middleware lands. The
third-port architecture exists precisely so admin can stay on localhost
even when gRPC is bound to the LAN.

## Panels

- **Header** — daemon name, status pill (green when at least one backend
  is `Ready`, yellow when degraded/disconnected, red when all are in
  `Error`), uptime, and the three ports the daemon is listening on.
  Updates every second; the status pill also updates live on backend
  lifecycle events.

- **Backends** — one card per registered backend. Shows id / kind /
  display name, status pill, capability badges, calibration state,
  concurrency model (`Policy` and `MaxConcurrent`), zone counts, and
  live in-flight sensation count. Each card has a **Stop all** button
  that calls `TriggerCoordinator.StopBackendAsync(id)`.

- **Sensation tester** — pick backend, pick sensation, click **FIRE**.
  Optional overrides (collapsed by default): intensity scale (0–200%),
  priority, trace ID. The result line shows `accepted=true` plus the
  resolved sensation id, or `accepted=false` plus the error code/field/
  message on rejection.

- **Recent triggers** — last 50 triggers, backfilled from the history
  database on page load and live-appended on
  `SensationCompleted` / `SensationCancelled` events. Click a row to
  expand for full forensic detail (sensation id, zones, intensity,
  priority, trace, error).

- **Panic button** — big red button at the bottom. Calls
  `TriggerCoordinator.StopAsync(All=true)` — the same code path the
  `/panic` HTTP endpoint uses. Increments the session panic counter
  rendered below the button. **Press `Esc`** anywhere on the page to fire
  the same handler.

## Troubleshooting

- **Blank page** — hard refresh (Cmd-Shift-R / Ctrl-Shift-R). Blazor's
  bundle is fingerprinted; this picks up the new one.
- **"Reconnecting…"** — daemon was restarted. Page recovers automatically
  once Blazor's SignalR reconnects.
- **"no triggers yet" on Recent triggers** — either no triggers have run
  this session and history is empty, or `Smited:History:Enabled = false`.
  Check the daemon's startup banner: a `History  disabled` line means
  the panel will always be empty.
- **Admin port refuses connection** — check `Smited:Admin:Enabled`. When
  false, the listener isn't started.
