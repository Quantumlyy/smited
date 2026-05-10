# Running smited

## Prerequisites

- .NET 9 SDK (the repo pins `9.0.100+` via `global.json` with `rollForward: latestFeature`).
- `grpcurl` for poking the gRPC surface (`brew install grpcurl`).

## Local run

```sh
dotnet run --project src/Smited.Daemon
```

Expected log output (Production environment):

```
[INF] Registered backend mock-owo (owo_skin: Mock OWO Skin)
[INF] Sensation library loaded: 5 entries across 1 backend(s).
[INF] Now listening on: http://127.0.0.1:7777
[INF] Now listening on: http://127.0.0.1:7778
[INF] Application started. Press Ctrl+C to shut down.
[INF] Hosting environment: Production
╭─smited──────────────────────────────────────────────╮
│ Listening   gRPC 127.0.0.1:7777 (h2c, reflection on)│
│ Panic       POST http://127.0.0.1:7778/panic        │
│ Backends    1 registered                            │
│ Body map    Not configured (warnings off)           │
│ Sensations  5 loaded                                │
╰─────────────────────────────────────────────────────╯
```

Logs roll daily into `logs/smited-YYYYMMDD.log` next to the binary. Set `ASPNETCORE_ENVIRONMENT=Development` to use `appsettings.Development.json` (binds `0.0.0.0`, increases log level to `Debug`).

## Smoke tests

```sh
grpcurl -plaintext localhost:7777 list                                         # smited.v1.SmitedService listed
grpcurl -plaintext localhost:7777 smited.v1.SmitedService/Health               # daemon_running=true
grpcurl -plaintext -d '{"sensation_name":"compile_error_mild","backend_id":"mock-owo"}' \
  localhost:7777 smited.v1.SmitedService/Trigger                               # accepted=true, sensation_id
curl -i http://localhost:7778/panic                                            # 200 {"ok":true,"stopped":N}
```

Tail the log file to see the simulated firing:

```sh
tail -f logs/smited-*.log
# [INF] Mock OWO firing afccef638... (compile_error_mild) on pectoral_l for 00:00:00.4000000
```

## Streamdeck panic button

smited exposes the emergency-stop endpoint on a separate HTTP/1.1 listener so a wedged gRPC pipeline can't take it down with it:

```
POST http://<host>:7778/panic
```

`POST`-only — `GET` returns `405 Method Not Allowed` so a stray `<img src=…>` tag can't accidentally fire the panic button.

By default the daemon binds to `127.0.0.1`, so the panic endpoint is reachable only from the same machine. To use Streamdeck Companion on a different machine, set `Smited.BindAddress` to `0.0.0.0` in `appsettings.json` and ensure the LAN segment is trusted. **The endpoint is not authenticated** — LAN-only is the access control. Don't expose `:7778` to the public internet.

In Companion, add a button with the **Generic HTTP** action:

- **Method:** `POST`
- **URL:** `http://<smited-host>:7778/panic`
- **Headers:** none
- **Body:** empty

The endpoint returns:

```json
{ "ok": true, "stopped": 3 }
```

Companion's response parser can render `stopped` on the button face if you want the visual feedback.

Every panic invocation logs a `Critical` line:

```
[FTL] PANIC stop requested from 192.168.1.42 (UA: Companion/3.x)
[FTL] PANIC stop completed from 192.168.1.42, 3 sensation(s) cancelled
```

Filter Serilog's pipeline on `LogLevel >= Critical` to route panics to a separate sink (alerts, webhooks, etc.) without touching the rest of the daemon's logging.

## User configuration

smited reads optional user-scoped configuration from a platform-specific path:

- **macOS / Linux**: `$XDG_CONFIG_HOME/smited/config.json`, defaulting to `~/.config/smited/config.json`.
- **Windows**: `%APPDATA%\smited\config.json`.

The file is created on first run with all keys commented out. Edit it, uncomment what you want to override, and restart the daemon. Values here win over `appsettings.json` defaults. Config is not hot-reloaded — restart after editing.

## Configuration

Defaults live in `src/Smited.Daemon/appsettings.json` and the spec is documented inline. Key knobs:

| Path | Default | Notes |
|---|---|---|
| `Smited:GrpcPort` | `7777` | gRPC h2c listener |
| `Smited:PanicPort` | `7778` | `/panic` HTTP/1.1 listener |
| `Smited:BindAddress` | `127.0.0.1` | Flip to `0.0.0.0` for LAN |
| `Smited:EnableReflection` | `true` | grpcurl-friendly |
| `Smited:Backends:Items` | _array, see below_ | Backends to bring online at startup |
| `Smited:BodyMap:OverlapPolicy` | `Warn` | `Warn` / `Refuse` / `Off`; see [`docs/body-map.md`](body-map.md) |
| `Smited:BodyMap:Placements` | _array_ | Per-backend zone-to-region declarations |
| `Smited:BodyMap:AllowOverrideRegions` | `[]` | Opt out of smited's default forbidden regions |
| `Smited:Sensations:LibraryRoot` | `./sensations` | Resolved relative to the binary |
| `Smited:EventBus:BufferCapacity` | `1024` | Per-subscriber channel capacity |
| `Smited:EventBus:SlowSubscriberPolicy` | `drop_oldest` | Channel `FullMode` for slow consumers |
| `Smited:History:Enabled` | `true` | Set false to skip the history DB entirely |
| `Smited:History:RetentionDays` | `30` | Days to keep rows; `0` = forever |
| `Smited:History:CustomPath` | _unset_ | Override the SQLite path |

History is daemon-internal — see [`docs/history.md`](history.md) for the schema and example queries. The bodymap is also daemon-internal — see [`docs/body-map.md`](body-map.md) for what placements do and the region taxonomy.

### Backend descriptors

Backends used to be enabled with per-backend booleans (`EnableMockOwo`, `EnableOwo`). The current shape is a typed array of descriptors — each descriptor names a kind and an instance id, and the daemon dispatches it to the matching `IBackendFactory`. Per-instance configuration (e.g. OWO's `GameDisplayName`, `ManualIp`) lives under the descriptor's `Options` sub-section.

Mock-only:

```json
{ "Smited": { "Backends": { "Items": [
  { "Kind": "mock_owo", "Id": "mock-owo", "Enabled": true }
] } } }
```

Real OWO only (Windows host):

```json
{ "Smited": { "Backends": { "Items": [
  {
    "Kind": "owo_skin",
    "Id": "owo-primary",
    "Enabled": true,
    "Options": {
      "GameDisplayName": "smited haptic daemon",
      "ManualIp": "192.168.1.42",
      "MaxReconnectAttempts": 3,
      "HeartbeatSeconds": 5
    }
  }
] } } }
```

Both side-by-side:

```json
{ "Smited": { "Backends": { "Items": [
  { "Kind": "mock_owo", "Id": "mock-owo", "Enabled": true },
  { "Kind": "owo_skin", "Id": "owo-primary", "Enabled": true,
    "Options": { "GameDisplayName": "smited haptic daemon" } }
] } } }
```

`Enabled: false` keeps a descriptor in the file but skips registration — useful when you want to hold onto the configuration for hardware you've temporarily disconnected.

### Disabling the default mock backend

`appsettings.json` ships with `Smited:Backends:Items` set to an empty array. When the configured `Items` is empty (or missing entirely), the daemon synthesizes a default `mock_owo` descriptor with id `mock-owo` and logs the synthesis at startup. This keeps "just run the daemon" frictionless while letting any non-empty `Items` array opt out cleanly.

To run with only the real OWO backend (no mock), provide an explicit non-empty `Items` array containing only the OWO descriptor:

```json
{
  "Smited": {
    "Backends": {
      "Items": [
        {
          "Kind": "owo_skin",
          "Id": "owo-primary",
          "Enabled": true,
          "Options": {
            "GameDisplayName": "smited haptic daemon"
          }
        }
      ]
    }
  }
}
```

To run with no backends at all (uncommon — the daemon's gRPC surface will report no registered backends), pin the array to a disabled placeholder so it stays non-empty without registering anything:

```json
{
  "Smited": {
    "Backends": {
      "Items": [
        { "Kind": "no_op", "Id": "placeholder", "Enabled": false }
      ]
    }
  }
}
```

The `Enabled: false` keeps the descriptor from tripping the "no factory registered for kind" warning at registration time; the validator itself only requires `Kind` and `Id` to be present.

### Migrating from the old boolean shape

If you have an existing user-config file from before the descriptor refactor, replace each boolean with the equivalent descriptor entry:

| Before | After |
|---|---|
| `"EnableMockOwo": true` | `{ "Kind": "mock_owo", "Id": "mock-owo", "Enabled": true }` |
| `"EnableOwo": true` plus `"Owo": { ... }` | `{ "Kind": "owo_skin", "Id": "owo-primary", "Enabled": true, "Options": { ... } }` |
| `"EnableMockOwo": false` | omit the descriptor entirely, or set `"Enabled": false` |

The daemon does not read `EnableMockOwo`, `EnableOwo`, or `Owo` anymore. A configuration that still uses those keys will silently start with zero backends registered.
