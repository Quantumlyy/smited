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
╭─smited───────────────────────────────────────────────╮
│ Listening   gRPC 127.0.0.1:7777 (h2c, reflection on) │
│ Panic       POST http://127.0.0.1:7778/panic         │
│ Backends    1 registered                             │
│ Sensations  5 loaded                                 │
╰──────────────────────────────────────────────────────╯
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
GET/POST http://<host>:7778/panic
```

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

## Configuration

Defaults live in `src/Smited.Daemon/appsettings.json` and the spec is documented inline. Key knobs:

| Path | Default | Notes |
|---|---|---|
| `Smited:GrpcPort` | `7777` | gRPC h2c listener |
| `Smited:PanicPort` | `7778` | `/panic` HTTP/1.1 listener |
| `Smited:BindAddress` | `127.0.0.1` | Flip to `0.0.0.0` for LAN |
| `Smited:EnableReflection` | `true` | grpcurl-friendly |
| `Smited:Backends:EnableMockOwo` | `true` | Always-on mock for development |
| `Smited:Backends:EnableOwo` | `false` | Real OWO; Windows-only |
| `Smited:Sensations:LibraryRoot` | `./sensations` | Resolved relative to the binary |
| `Smited:EventBus:BufferCapacity` | `1024` | Per-subscriber channel capacity |
| `Smited:EventBus:SlowSubscriberPolicy` | `drop_oldest` | Channel `FullMode` for slow consumers |
