# OWO Skin integration

smited's `OwoBackend` (in `src/Smited.Daemon.Owo/`) wraps the official
OWO C# SDK to drive a calibrated OWO Skin haptic vest. This document
covers prerequisites, configuration, the smoke-test runbook, and the
manufacturer's TENS safety notes.

## Prerequisites

- **Windows 10 or 11.** The OWO SDK and the MyOWO desktop app are
  Windows-only. Mac/Linux daemons run with `MockOwoBackend` only —
  there is no shim available for the real device on those platforms.
- **An OWO Skin device, paired and fully calibrated** through the MyOWO
  app. Calibration is mandatory: the SDK refuses to send sensations to
  an uncalibrated user, and the daemon's `ConnectAsync` will hang
  until calibration completes.
- **MyOWO running in the background** for the entire lifetime of the
  daemon. The SDK talks to MyOWO over local TCP; the app then holds the
  Bluetooth pairing with the device. Closing MyOWO drops the
  daemon→device transport.

## First-time setup

1. Install MyOWO from `https://owogame.com/downloads`.
2. Sign in (a free account is sufficient).
3. Pair the device via MyOWO's "Devices" tab — Bluetooth pairing is
   driven entirely by the MyOWO app.
4. Run the calibration flow. **Do not skip this.** Calibration sets
   your personal max intensity; the SDK's `intensityPercentage` is
   interpreted as a percent of *that* maximum, so calibration is the
   safety boundary.
5. Open MyOWO's "Scan Games" panel — leave it open during smited
   startup so the daemon's "smited haptic daemon" entry appears for
   pairing.

## Enabling in smited

Edit your user config file (`%APPDATA%\smited\config.json`):

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
            "GameDisplayName": "smited haptic daemon",
            "ManualIp": null,
            "MaxReconnectAttempts": 3,
            "HeartbeatSeconds": 5
          }
        }
      ]
    }
  }
}
```

This replaces the default `mock_owo` descriptor that ships in
`appsettings.json` (user-config keys win over the defaults), so the
mock and real backends don't both register and serve
`owo_skin`-kinded sensations. To run them side-by-side, list both as
descriptors in the same `Items` array. Restart the daemon. The
startup banner shows `owo-primary` registered. Switch to MyOWO's
"Scan Games" panel and pick `smited haptic daemon` from the list —
the daemon transitions to `BACKEND_STATUS_READY` once pairing
completes.

`ManualIp` skips the auto-discovery handshake when set. Use it when
MyOWO runs on a different machine on your LAN, or when auto-discovery
fails because of multicast filtering on your network.

## Smoke-test runbook

After the pairing handshake completes, verify the wiring with
`grpcurl`:

```sh
grpcurl -plaintext localhost:7777 smited.v1.SmitedService/Health
# Expected: backends includes owo-primary with status BACKEND_STATUS_READY.

grpcurl -plaintext -d '{"backend_id":"owo-primary"}' \
    localhost:7777 smited.v1.SmitedService/DescribeBackend
# Expected: 10 zones (pectoral, abdominal, lumbar, dorsal, arm — L/R),
# 6 parameters, calibration { calibrated: true, last_calibrated_at: ... }.

grpcurl -plaintext -d '{
    "backend_id":"owo-primary",
    "sensation_name":"compile_error_mild"
}' localhost:7777 smited.v1.SmitedService/Trigger
# Expected: accepted=true. You feel a 0.4s zap on both pectorals.
```

Iterate the rest of the sample library:

| Sensation              | Expected feel                                    |
| ---------------------- | ------------------------------------------------ |
| `compile_error_mild`   | Short pectoral zap                               |
| `compile_error_severe` | Stronger, longer pectoral hit                    |
| `test_failed`          | Two-pulse dorsal pattern                         |
| `deploy_success`       | Gentle ramped wave across the torso              |
| `chat_zap`             | Quick, localized hit                             |

## Troubleshooting

**`accepted=true` but no sensation on skin**
1. MyOWO still showing "Connected" for the device? If not, re-pair via
   MyOWO's Devices tab.
2. Re-run calibration. Pads degrade over time; stale calibration may
   read as too low to perceive.
3. Check the gel pads. They need direct skin contact and lose
   conductivity after roughly 50 sessions. Replace if older.

**`INTERNAL` gRPC error or `accepted=false`**
- Check `%LOCALAPPDATA%\smited\logs\smited-*.log` for the SDK
  exception. The most common cause is MyOWO closing in the
  background — restart MyOWO and the daemon will reconnect on the
  next heartbeat tick (default 5 s).

**Daemon banner shows OWO as registered but `Health` reports
`DISCONNECTED`**
- Transport drop detected by the heartbeat poll. Check MyOWO is
  running. The daemon attempts reconnect with exponential backoff up
  to `MaxReconnectAttempts`; on exhaustion `Status` flips to `ERROR`
  and you must restart the daemon.

**Banner doesn't show OWO at all on Windows**
- Confirm an `owo_skin` descriptor exists in `Smited:Backends:Items`
  with `Enabled: true`, and that `Smited.Daemon.Owo.dll` is in the
  daemon's output directory. The daemon project copies it as content
  on Windows builds. If the descriptor is present but the banner still
  shows zero backends, check the log for `Factory for kind owo_skin
  declined to create descriptor` — that means the assembly's runtime
  dependency (`OWO.dll`) is missing; rebuild/republish to refresh the
  output directory.

## Panic button

The `/panic` HTTP endpoint cancels every active sensation across every
backend, including OWO. Use this from a Stream Deck button:

```
POST http://127.0.0.1:7778/panic
```

The endpoint logs at `Critical` level and is independent of the gRPC
pipeline, so it works even when gRPC is wedged. Internally it calls
`OwoBackend.StopAsync` with `All=true`, which cancels every tracked
sensation and calls the SDK's `Stop()` to silence the device
immediately.

## Safety notes

OWO Skin uses TENS / EMS — electrical muscle stimulation. The device
manufacturer's safety documentation applies. Read it. Highlights:

- Do not use with a pacemaker or any implanted electronic device.
- Do not use during pregnancy.
- Do not use over broken or irritated skin.
- Stop immediately if you feel anything unusual.

smited's `intensity` parameter is interpreted by the SDK as a
percentage of *your* calibrated maximum. The SDK refuses to exceed your
calibrated maximum regardless of what smited sends — that is the
safety boundary, and smited honors it but does not enforce it
independently. Recalibrate any time the device feels off.
