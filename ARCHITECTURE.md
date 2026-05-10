# smited — architecture

The daemon is a thin layer between gRPC clients and pluggable haptic backends. Every architectural choice is in service of two invariants:

1. **Clients never hardcode hardware-specific knowledge.** Backends advertise their zones, parameter schemas, concurrency models, and calibration state at runtime; clients discover and dispatch.
2. **The wire format is fixed.** It lives in a separate repo at `buf.build/quantumly-labs/smited:v0.1.0` and is consumed via `buf generate` with the output committed under `gen/csharp/`. Schema changes never originate here.

## Layer diagram

```
                ┌────────────────────────────────────────────┐
                │           gRPC clients (LAN/localhost)      │
                │   Stream Deck • multiplexer • CI hook       │
                └───────────────┬─────────────┬──────────────┘
                                │             │ HTTP/1.1 GET/POST
                                │ HTTP/2 h2c  │
                ┌───────────────▼─────────────▼──────────────┐
                │                Kestrel                     │
                │   :7777 (gRPC)                :7778 (panic)│
                └───────────────┬─────────────┬──────────────┘
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
   └──────────┬───┘  └──────┬─────────┘  └─────────────────┘ └─────────────┘
              │             │
              │             ▼
              │   ┌─────────────────────────────────────────┐
              │   │           IHapticBackend                │
              │   │ ─────────────────────────────────────── │
              │   │  MockOwoBackend       │ OwoBackend      │
              │   │  MockBhapticsBackend  │ BhapticsBackend │
              │   │   (in-process mocks)  │ (Windows-only,  │
              │   │                       │ reflectively    │
              │   │                       │ loaded)         │
              │   └─────────────────────────────────────────┘
              │              │
              │              │ backend.Events  (per-backend cold stream)
              │              ▼
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

**Backend heterogeneity.** OWO and bHaptics differ on every meaningful axis: TENS vs vibration motors, exclusive `CANCEL_OLDEST` (max 1) vs motor-summing `PRIORITY` (max 4), per-user calibration vs Player-slider intensity, 10 zones vs 40+ vest motors plus optional accessories. The schema accommodates both because every descriptor (`ZoneTopology`, `ParameterSchema`, `ConcurrencyModel`, `CalibrationState`) is per-backend and discovered at runtime — clients never assume a body of zone names or a single concurrency policy. Adding a third family with different semantics again should be the same shape: pick a `Kind`, fill in the descriptors, ship.

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

`StartupBanner` renders a Spectre.Console panel after `ApplicationStarted` showing both ports, backend count, and sensations loaded count.

## Cross-platform conditional compilation

`Smited.Daemon.Owo.csproj` targets `net9.0-windows`. Its OWO NuGet package and the SDK-touching files (`OwoBackend.cs`, `StaticOwoSdk.cs`, `OwoMuscleMap.cs`) are guarded by the `_TargetingWindows` MSBuild property (defined in `Directory.Build.props`), which is true when either the host is Windows or the build was given a `win-*` `RuntimeIdentifier` — that's the correct gate for "include the Windows-only assets," and matches the Cake `Publish-Win-x64` task that runs on CI from Linux. Gating on `'$(OS)' == 'Windows_NT'` (the build host) is wrong because it silently drops the OWO assembly from cross-publishes. The daemon project's reverse `ProjectReference` is gated the same way and uses `ReferenceOutputAssembly=false` so the compile-time graph stays acyclic — `BackendBootstrapper` loads `OwoBackend` via `Type.GetType("Smited.Daemon.Owo.OwoBackend, Smited.Daemon.Owo")` at runtime when `Smited:Backends:EnableOwo` is true and the assembly is in the output directory. The matching `IOwoSdk` registration in `Program.cs` follows the same reflective pattern for `StaticOwoSdk`. See [`docs/adding-a-backend.md`](docs/adding-a-backend.md) for the full pattern any new platform-specific backend should follow.

The `IOwoSdk` interface and the `OwoSendCommand` record live in `Smited.Daemon.Abstractions` so both the daemon host and the Windows-only OWO project can reference them without anyone forcing a Mac-side compile dependency on the Windows assembly. Tests that need to construct `OwoBackend` directly (Trigger/Stop/heartbeat behavior) take a `_TargetingWindows`-gated `ProjectReference` to `Smited.Daemon.Owo` from the test csproj and are excluded from compile when not targeting Windows.

`Smited.Daemon.Bhaptics.csproj`, by contrast, targets plain `net9.0`. The bHaptics integration uses BCL `System.Net.WebSockets.ClientWebSocket` and has no Windows-only build dependencies, so the assembly is platform-portable and its reverse `ProjectReference` from the daemon is unconditional. The Windows-only property of bHaptics is purely runtime — bHaptics Player only runs on Windows — and `BackendBootstrapper` enforces it with an `OperatingSystem.IsWindows()` check before attempting the reflective `Type.GetType("Smited.Daemon.Bhaptics.BhapticsBackend, Smited.Daemon.Bhaptics")` load. The cross-platform compilation pays off in test coverage: the protocol DTOs, `PlayerClient`, and `ZoneIndexMap` get full unit-test coverage on Mac CI against a Kestrel-backed simulator, leaving only the on-hardware smoke test as Windows-bound.

## Things explicitly out of scope

- TLS, authentication.
- A multiplexer, Stream Deck integration, OBS integration. Those are downstream consumers of this daemon.
- New RPCs, message types, or fields beyond `v0.1.0`. Schema changes start in `Quantumlyy/smited-schema`.
