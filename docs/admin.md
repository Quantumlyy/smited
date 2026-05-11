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
  that cancels every active sensation on that backend.

- **Sensation tester** — pick backend, pick sensation, click **FIRE**.
  The result line shows `accepted=true` plus the resolved sensation id,
  or `accepted=false` plus the error code/field/message on rejection.
  Optional overrides (collapsed by default):
  - **Intensity scale (`0..100`)** — override the sensation's authored
    `default_intensity`. Leave empty to use the sensation's default;
    supply a value to override. Range matches the gRPC
    `intensity_scale` contract; values above 100 are not supported.
  - **Priority** — integer; higher preempts lower under the
    `PRIORITY` concurrency policy. No documented range; pass through.
  - **Trace ID** — echoed back on lifecycle events. Auto-generated
    GUID if left blank.

- **Recent triggers** — last 50 triggers, backfilled from the history
  database on page load and live-appended on
  `SensationCompleted` / `SensationCancelled` events. Click a row to
  expand for full forensic detail (sensation id, zones, intensity,
  priority, trace, error). Backfill rows are labelled `Accepted` /
  `Rejected` rather than `Completed` / `Failed` because the trigger
  record only knows whether the coordinator accepted the call — the
  final outcome lives in the lifecycle event stream that produces the
  live rows.

- **Panic button (latching circuit breaker)** — big red button at
  the bottom. Two effects on every click:
  1. Stop all in-flight sensations across every backend — the same
     code path the `/panic` HTTP endpoint uses, dispatched through
     the in-process `SmitedActionService` so admin-fired and
     HTTP-fired panics produce identical history rows and
     CRITICAL-level audit log lines.
  2. **Latch the daemon-wide breaker.** Subsequent `Trigger` calls
     reject with `BackendUnavailable` and a `BREAKER_TRIPPED:` message
     prefix until an admin re-arms the breaker. Stops, the panic HTTP
     endpoint, status reads, history queries, and event streams stay
     unaffected — only `Trigger` is gated.

  Increments the session panic counter rendered below the button.
  **Press `Esc`** anywhere on the page to fire the same handler.

  When the breaker is tripped, a **PANIC LATCHED** banner appears
  below the header with a "Re-arm" button. Re-arm flow is two-step
  with a single-use challenge so accidental clicks (during a panic,
  shaky hand, spam-clicking) can't accidentally re-enable triggers:

  1. Click "Re-arm" in the header banner. A "Re-arm daemon?"
     confirmation dialog opens.
  2. Click "Yes, re-arm". The dialog requests a single-use challenge
     from the daemon (valid for 30 seconds).
  3. Click "Confirm re-arm". The challenge is consumed atomically;
     the breaker resets and triggers resume. An expired or already-
     consumed token bounces back to step 1 with a visible error.

  The Sensation tester FIRE button is disabled while the breaker is
  tripped, with a muted helper line pointing at the header banner.

  The breaker is daemon-wide: any latch blocks every backend's
  triggers. Per-backend breakers are out of scope for v1; a future
  PR may add them if multi-backend deployments call for it. The
  breaker resets on daemon restart (in-memory only).

  ### Breaker REST API

  External tools can drive the breaker without depending on the Blazor
  UI. Three endpoints live on the admin port:

  - `GET /admin/breaker` — current state (`tripped`, `trippedAt`,
    `reason`).
  - `POST /admin/breaker/rearm/challenge` — generate a single-use
    challenge token; response is `{ challenge, expiresAt }`.
  - `POST /admin/breaker/rearm` — body `{ "challenge": "..." }`;
    verifies the challenge, consumes it, and re-arms. Returns 400
    on invalid or expired challenges.

  The admin UI uses the underlying `IBreakerService` /
  `IBreakerChallengeService` directly via injection rather than going
  through HTTP; the endpoints exist for future external clients.

  Wire-format note: the `Trigger` rejection rides on the existing
  `TriggerErrorCode.BackendUnavailable` enum value with a
  `BREAKER_TRIPPED:` message prefix so external clients can pattern-
  match on the prefix without depending on a wire-schema bump
  (the schema is pinned at `buf.build/quantumly-labs/smited:v0.1.0`).

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
