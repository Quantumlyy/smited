# smited — architecture

The daemon is a thin layer between gRPC clients and pluggable haptic backends. Every architectural choice is in service of two invariants:

1. **Clients never hardcode hardware-specific knowledge.** Backends advertise their zones, parameter schemas, concurrency models, and calibration state at runtime; clients discover and dispatch.
2. **The wire format is fixed.** It lives in a separate repo at `buf.build/quantumly-labs/smited:v0.1.0` and is consumed via `buf generate` with the output committed under `gen/csharp/`. Schema changes never originate here.

## Layer diagram

```
                ┌────────────────────────────────────────────┐
                │           gRPC clients (LAN/localhost)      │
                │   Stream Deck • multiplexer • CI hook       │
                └───────────────┬─────────────┬─────────┬────┘
                                │             │ HTTP/1.1│ HTTP/1.1
                                │ HTTP/2 h2c  │   POST  │   GET
                ┌───────────────▼─────────────▼─────────▼────┐
                │                Kestrel                     │
                │   :7777 gRPC   :7778 panic   :7779 admin   │
                └───────────────┬─────────────┬─────────┬────┘
                                │             │
              ┌─────────────────▼──┐   ┌──────▼─────────────────┐
              │ ProtovalidateInterc│   │ PanicEndpoint          │
              │ — runs buf.validate│   │ — bypasses interceptor │
              │   annotations      │   │ — Critical-level audit │
              └─────────────────┬──┘   └──────┬─────────────────┘
                                │             │
                ┌───────────────▼─────────────▼──────────────┐
                │       SmitedGrpcService + ProtoMappers     │
                │   (translates wire <-> internal records)   │
                └───────────────┬────────────────────────────┘
                                │
              ┌─────────────────┼────────────────┬───────────────┐
              │                 │                │               │
   ┌──────────▼───┐  ┌──────────▼─────┐  ┌──────▼──────────┐ ┌──▼──────────┐
   │ BackendRegis │  │TriggerCoordin- │  │ SensationLibrary│ │ EventBus    │
   │ try          │  │ ator           │  │  (in-memory +   │ │  (channels +│
   │              │  │  + Concurrency │  │   on-disk JSON) │ │  drop-old)  │
   │              │  │   Enforcer     │  │                 │ │             │
   │              │  │  + IBodyMap-   │  │                 │ │             │
   │              │  │    State (over-│  │                 │ │             │
   │              │  │    lap check)  │  │                 │ │             │
   └──────────┬───┘  └──────┬─────────┘  └─────────────────┘ └─────────────┘
              │             │
              │             ▼
              │   ┌─────────────────────────────────────────┐
              │   │           IHapticBackend                │
              │   │ ─────────────────────────────────────── │
              │   │  MockOwoBackend │ OwoBackend (Windows,  │
              │   │                 │  reflectively loaded) │
              │   └─────────────────────────────────────────┘
              │              ▲
              │              │ built by IBackendFactory.TryCreate
              │   ┌──────────┴──────────────────────────────┐
              │   │      BackendBootstrapper                │
              │   │  iterates BackendsOptions.Items[]       │
              │   │  resolves IBackendFactory by Kind       │
              │   │  then runs BodyMapValidator over the    │
              │   │  registered set (refuses backends in    │
              │   │  forbidden regions, populates           │
              │   │  BodyMapState for the coordinator)      │
              │   └────┬───────────────────────────────────┘
              │        │
              │        │ backend.Events  (per-backend cold stream)
              │        ▼
              │   ┌─────────────────────────────────────────┐
              └──>│  BackendBootstrapper fan-out task       │
                  │  forwards every backend event to Bus    │
                  └─────────────────────────────────────────┘
```

## Components

### gRPC layer (`Services/`)

`SmitedGrpcService` inherits from the generated `SmitedService.SmitedServiceBase` and implements the nine RPCs by delegating to coordinator/registry/library/eventBus. All wire ↔ domain translation lives in `ProtoMappers`; service handlers stay thin.

**Validation split:**

- `ProtovalidateInterceptor` runs `buf.validate` annotations against every incoming request via the `ProtoValidate` library. Violations come back as `INVALID_ARGUMENT` with a path-prefixed message listing every failed rule.
- Domain rejections (unknown zone, parameter type mismatch, sensation_name not in library, missing `sensation_registry_mutable` capability) come back inside the success-shaped response body — `TriggerResponse{accepted=false, error=...}`, `RegisterSensationResponse{registered=false, error=...}` — **never** as gRPC errors. Domain rejection is a normal outcome, not a transport error.

### Backend abstraction (`Backends/`)

`IHapticBackend` is the central abstraction. Every backend implements it. Static descriptors (zones, parameter schema, concurrency model, calibration, extras) reuse generated proto types directly because they round-trip 1:1 to the wire. Triggering and lifecycle flows use **internal records** (`BackendTriggerRequest`, `BackendTriggerResult`, `BackendStopRequest`, `BackendEvent` hierarchy) so backends aren't coupled to wire details.

`BackendRegistry` is `internal` — nothing outside the daemon should hold a registry reference; tests reach it via `InternalsVisibleTo`. Register and deregister publish a `BackendLifecycleEvent` through `IBackendEventSink`, which `EventBus` implements.

### Descriptor + factory model

`BackendBootstrapper` does not special-case any backend kind. Configuration declares an array of `BackendDescriptor` entries under `Smited:Backends:Items[]`; each entry names a `Kind` (case-insensitive discriminator), an `Id` (unique runtime identity), an `Enabled` flag, and an optional `Options` sub-section. The bootstrapper resolves an `IBackendFactory` whose `Kind` matches and asks it to build the backend, passing the descriptor and the raw options-section so the factory can bind to its own kind-specific options type. Factories that decline to instantiate (return `null`) are logged and skipped — this is how platform-conditional backends like OWO opt out on the wrong OS without aborting startup. `BackendDescriptorValidator` runs at the top of `StartAsync` and aborts on duplicate ids, malformed ids, or more than one `mock_owo` descriptor (the singleton constraint). Descriptor and factory types live in `Smited.Daemon.Abstractions` so platform-conditional backend assemblies can implement them without a compile-time dependency on the daemon host. See [`docs/adding-a-backend.md`](docs/adding-a-backend.md).

### Bodymap framework (`BodyMap/`)

`BodyMapValidator` runs after every descriptor has registered. For each `Smited:BodyMap:Placements` entry — a (`BackendId`, `ZoneIds[]`, `Region`) tuple — it checks the placement against the backend's manufacturer-mandated `IHapticBackend.ForbiddenRegions` (non-overridable) and smited's own `SmitedDefaultForbiddenRegions` (overridable via `AllowOverrideRegions`). The check uses `RegionHierarchy.Overlaps` symmetrically so subregion declarations cannot bypass parent-region bans and parent declarations cannot bypass child-region bans. Validation also catches duplicate (post-group-expansion) leaf zones, empty `ZoneIds` lists, and unknown backend/zone references. Every error kind except `BackendDeclined` (placement targets a declared backend whose factory legitimately declined, e.g. `owo_skin` on Mac) is fatal-throw — the daemon refuses to start until the user fixes the configuration. The validator's output (per-backend region set, per-region backend set, per-zone region map) lands in the `BodyMapState` daemon singleton; `TriggerCoordinator` reads `OverlapPolicy` and calls `CheckOverlap` on every trigger to enforce the `Refuse` policy at dispatch time. See [`docs/body-map.md`](docs/body-map.md) for the user-facing taxonomy and policy semantics.

### Trigger coordinator (`Triggering/`)

Sits between the gRPC layer and backends. Resolves the target backend, looks up named sensations (or accepts inline microsensations), validates zones and parameters against the backend's schema, applies concurrency, dispatches to the backend, and tracks active sensations so `Stop` can cancel them.

`ConcurrencyEnforcer` keeps per-backend state and implements the four policies:

- **REJECT_NEW** — return rate-limited at capacity.
- **CANCEL_OLDEST** — preempt the earliest-started sensation when at capacity.
- **PRIORITY** — preempt the lowest-priority active sensation only when the candidate's priority is strictly higher; otherwise reject.
- **QUEUE** — await a free slot via a per-backend `SemaphoreSlim`.

### EventBus (`Events/`)

Single source of truth for daemon-wide backend events. Backends emit lifecycle events on their own `IAsyncEnumerable<BackendEvent>`; `BackendBootstrapper` spins up a fan-out task per backend that forwards every event into `EventBus`. The gRPC `SubscribeEvents` handler subscribes via `EventStream` and applies kind/backend_id filters on the subscriber side.

Each subscriber gets its own bounded `Channel<BackendEvent>` with `FullMode = DropOldest` (configurable via `Smited:EventBus:SlowSubscriberPolicy`) — a slow consumer can't back-pressure the bus or block other consumers.

### Sensation library (`Sensations/`)

JSON files under `LibraryRoot/<backend_kind>/*.json` are loaded at boot by `SensationLoader` (an `IHostedService` that runs synchronously in `StartAsync` after `BackendBootstrapper`). Each file is validated against the matching backend's `ParameterSchema` and `ZoneTopology`; any failure aborts the host with the file path and offending JSON path in the message.

`SensationLibrary` is the in-memory store, keyed by `(BackendId, Name)`. Mutations publish `SensationRegistryChangedEvent`.

`backend_kind` is a file-level binding to a backend family (e.g. `owo_skin`); the loader binds the sensation to **every** registered backend whose `Kind` matches, so one file works against any number of identically-typed backends.

### Diagnostics (`Diagnostics/`)

`PanicEndpoint` exposes `/panic` on a separate Kestrel listener (HTTP/1.1, default port 7778). Cancels every active sensation regardless of gRPC state. No auth; LAN/localhost binding is the access control. Logs every invocation at `Critical` so post-mortems have an immediate answer to "why did everything stop".

`StartupBanner` renders a Spectre.Console panel after `ApplicationStarted` showing all three listener ports (gRPC, panic, admin), backend count, bodymap status (`N placements[, M warning(s)]` or `Not configured (warnings off)`), sensation count, and history-database path (or `disabled`). Forbidden-region errors are fatal at startup, so by the time the banner renders the bodymap is valid — there's no "refused" state to display.

### Admin UI (`Admin/`)

Blazor Server pages on port 7779, hosted in the same `WebApplication` as the gRPC server and panic endpoint. Components inject daemon services (`BackendRegistry`, `TriggerCoordinator`, `EventBus`, `SensationLibrary`, history factory) directly — no gRPC roundtrip. Live updates ride Blazor Server's existing SignalR connection; per-component subscriptions to the in-process `EventBus` re-render on each event.

This means the admin UI has access to capabilities the gRPC schema doesn't expose (history queries, internal coordinator state). When those prove useful for external clients, a future schema bump exposes them; the admin UI doesn't wait. The Kestrel listener for the admin port is gated by `Smited:Admin:Enabled` (default true) so headless deployments can omit it.

Authentication is intentionally absent in v1: the admin port binds to `127.0.0.1`. The third-port architecture makes a future shared-secret middleware addition cheap (it goes in `MapWhen` on the admin branch only). See [`docs/admin.md`](docs/admin.md) for the panel reference and the no-auth caveat.

## Cross-platform conditional compilation

`Smited.Daemon.Owo.csproj` targets `net9.0-windows`. Its OWO NuGet package and the SDK-touching files (`OwoBackend.cs`, `OwoBackendFactory.cs`, `StaticOwoSdk.cs`, `OwoMuscleMap.cs`) are guarded by the `_TargetingWindows` MSBuild property (defined in `Directory.Build.props`), which is true when either the host is Windows or the build was given a `win-*` `RuntimeIdentifier` — that's the correct gate for "include the Windows-only assets," and matches the Cake `Publish-Win-x64` task that runs on CI from Linux. Gating on `'$(OS)' == 'Windows_NT'` (the build host) is wrong because it silently drops the OWO assembly from cross-publishes. The daemon project's reverse `ProjectReference` is gated the same way and uses `ReferenceOutputAssembly=false` so the compile-time graph stays acyclic — `BackendsServiceCollectionExtensions.AddOwoBackendIfWindows` loads `OwoBackendFactory` and `StaticOwoSdk` via `Type.GetType("Smited.Daemon.Owo.<Type>, Smited.Daemon.Owo")` at runtime; the factory is then registered in DI for `BackendBootstrapper` to dispatch to when an `owo_skin` descriptor appears. See [`docs/adding-a-backend.md`](docs/adding-a-backend.md) for the full pattern any new platform-specific backend should follow.

The `IOwoSdk` interface and the `OwoSendCommand` record live in `Smited.Daemon.Abstractions` so both the daemon host and the Windows-only OWO project can reference them without anyone forcing a Mac-side compile dependency on the Windows assembly. Tests that need to construct `OwoBackend` directly (Trigger/Stop/heartbeat behavior) take a `_TargetingWindows`-gated `ProjectReference` to `Smited.Daemon.Owo` from the test csproj and are excluded from compile when not targeting Windows.

## Things explicitly out of scope

- TLS, authentication.
- A multiplexer, Stream Deck integration, OBS integration. Those are downstream consumers of this daemon.
- New RPCs, message types, or fields beyond `v0.1.0`. Schema changes start in `Quantumlyy/smited-schema`.
