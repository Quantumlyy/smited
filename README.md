# smited

A daemon that fronts haptic hardware behind a single gRPC interface. Backends register at runtime and advertise their own zones, parameter schemas, concurrency models, and calibration state, so clients discover capabilities and dispatch accordingly without hardcoding hardware-specific knowledge. Real backends today: OWO Skin (Windows, TENS vest), and the bHaptics family — TactSuit X40 vest plus per-arm TactSleeve and per-foot Tactosy (Windows, vibrotactile). This MVP ships with mock backends for each kind so the gRPC surface, event streaming, and concurrency model can be exercised on Mac without hardware. Backend enablement is descriptor-driven (`Smited:Backends:Items[]`), and a daemon-internal bodymap framework knows where each backend's zones sit on the user's body and refuses placements in manufacturer-mandated or smited-default forbidden regions.

## Quickstart (macOS / Linux)

```sh
git clone https://github.com/Quantumlyy/smited.git
cd smited
dotnet run --project src/Smited.Daemon
```

The daemon binds:

- gRPC on `127.0.0.1:7777` (h2c)
- Emergency-stop HTTP on `127.0.0.1:7778`
- **Admin UI on `127.0.0.1:7779` — open in browser to verify end-to-end**

The startup banner shows all three:

```
╭─smited──────────────────────────────────────────────────╮
│ Listening   gRPC 127.0.0.1:7777 (h2c, reflection on)    │
│ Panic       POST http://127.0.0.1:7778/panic            │
│ Admin       http://127.0.0.1:7779/                      │
│ Backends    1 registered                                │
│ Body map    Not configured (warnings off)               │
│ Sensations  5 loaded                                    │
│ History     /Users/.../smited/history.db                │
╰─────────────────────────────────────────────────────────╯
```

Verify with `grpcurl` (install via `brew install grpcurl`):

```sh
grpcurl -plaintext localhost:7777 list                                         # smited.v1.SmitedService
grpcurl -plaintext localhost:7777 smited.v1.SmitedService/Health               # daemon_running=true
grpcurl -plaintext -d '{"sensation_name":"compile_error_mild","backend_id":"mock-owo"}' \
  localhost:7777 smited.v1.SmitedService/Trigger                               # accepted=true
```

The full grpcurl cheatsheet is in [`docs/grpcurl-cheatsheet.md`](docs/grpcurl-cheatsheet.md).

## Schema

The wire format is published at [`buf.build/quantumly-labs/smited`](https://buf.build/quantumly-labs/smited) and pinned to **`v0.1.0`** for this build. The schema source lives in [`Quantumlyy/smited-schema`](https://github.com/Quantumlyy/smited-schema). Generated C# is consumed via `buf generate` and committed under `gen/csharp/` for hermetic builds — no `buf` install required to build the daemon.

## Building

```sh
./build.sh                              # restore + build + test
./build.sh --target Publish-OSX-arm64   # self-contained single-file binary in artifacts/
```

See [`docs/building.md`](docs/building.md) for the full Cake target list.

## Documentation

- [`ARCHITECTURE.md`](ARCHITECTURE.md) — layer diagram, EventBus model, validation split, backend abstraction contract.
- [`docs/running.md`](docs/running.md) — running on Mac, expected log output, smoke tests, Streamdeck panic-button setup.
- [`docs/building.md`](docs/building.md) — Cake build script, publish targets, why Cake.
- [`docs/grpcurl-cheatsheet.md`](docs/grpcurl-cheatsheet.md) — example grpcurl invocations for every RPC.
- [`docs/adding-a-backend.md`](docs/adding-a-backend.md) — how to add a new `IHapticBackend` implementation, including the descriptor + factory pattern.
- [`docs/body-map.md`](docs/body-map.md) — bodymap framework, region taxonomy, and forbidden-region semantics.
- [`docs/owo.md`](docs/owo.md) — Windows OWO Skin setup, calibration, smoke-test runbook, TENS safety notes.
- [`docs/bhaptics.md`](docs/bhaptics.md) — Windows bHaptics setup (TactSuit, TactSleeve, Tactosy for Feet), smoke-test runbook, vibrotactile safety notes.
- [`docs/history.md`](docs/history.md) — the daemon's SQLite history database: tables, queries, retention.
- [`docs/admin.md`](docs/admin.md) — the in-process Blazor Server admin UI on port 7779 (smoke-test surface).
- [`sensations/README.md`](sensations/README.md) — sensation file format reference.

## Status

This MVP covers capability discovery, sensation triggering, the four concurrency policies (REJECT_NEW, CANCEL_OLDEST, PRIORITY, QUEUE), event streaming, the `/panic` endpoint, the boot-time sensation library, descriptor-driven backend enablement (`Smited:Backends:Items[]` dispatched through `IBackendFactory`), and a daemon-internal bodymap framework with manufacturer-mandated and smited-default forbidden-region enforcement. The Windows OWO Skin backend in `src/Smited.Daemon.Owo/` is wired against the official OWO C# SDK — see [`docs/owo.md`](docs/owo.md). The Windows bHaptics backend family in `src/Smited.Daemon.Bhaptics/` wraps the `Bhaptics.Tac` SDK1 NuGet for the TactSuit, TactSleeve, and Tactosy for Feet — see [`docs/bhaptics.md`](docs/bhaptics.md). The daemon is LAN/localhost only — no TLS, no auth.

## License

See [`LICENSE`](LICENSE).
