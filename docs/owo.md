# OWO Skin integration

smited's `OwoBackend` (in `src/Smited.Daemon.Owo/`) wraps the official
OWO C# SDK to drive a calibrated OWO Skin haptic vest. Two desktop
applications can sit between the daemon and the device:

- **OWO Visualizer** — accepts dev-mode auth, supports manual and
  auto-discovery connections. **The canonical development path** for
  smited contributors.
- **MyOWO consumer app** — production-grade. Requires a project ID and
  a signed `.owoauth` file, both gated on email approval from
  `devs@owogame.com`.

The Visualizer is what every smited contributor uses unless they
specifically need MyOWO consumer-app behavior. Treat it as a daemon
prerequisite for OWO development.

## Quick start (OWO Visualizer)

The OWO Visualizer is the recommended path for development and for any
user who hasn't registered a project ID with OWO. It accepts dev-mode
auth (any non-empty project ID), supports manual and auto-discovery
connections, and surfaces the device calibration / connection status
the daemon needs.

### Setup

1. Download the OWO Visualizer from
   [`https://owogame.com/developers/`](https://owogame.com/developers/)
   under the "Sensation creator" / "Tools" sections.
2. Power on the OWO Skin and pair it with the Visualizer the same way
   you would with MyOWO. Calibration runs the same flow.
3. In the Visualizer's main view, confirm the device battery indicator
   and calibration status are shown.

### smited configuration

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
            "ProjectId": "smited-dev",
            "ManualIp": "127.0.0.1",
            "ConnectTimeoutSeconds": 10,
            "MaxReconnectAttempts": 3,
            "HeartbeatSeconds": 5
          }
        }
      ]
    }
  }
}
```

`ProjectId` can be any non-empty string for the Visualizer; `smited-dev`
is the documented default. `ManualIp` set to loopback because the
Visualizer runs on the same machine as the daemon — auto-discovery
also works, but loopback skips the discovery handshake. The OWO FAQ
explicitly recommends manual connection if AutoConnect doesn't pair.
`ConnectTimeoutSeconds` bounds the SDK's connect handshake — if the
Visualizer doesn't accept the game within the deadline, the daemon
continues with the OWO backend in `BACKEND_STATUS_DISCONNECTED`, and
the heartbeat loop retries in the background.

This replaces the default `mock_owo` descriptor that ships in
`appsettings.json` (user-config keys win over the defaults). To run
both side-by-side, list both as descriptors in the same `Items` array.

### Smoke test

1. Run smited.
2. The Visualizer's "Scan Games" panel should show the project ID
   (`smited-dev`) within ~10 seconds.
3. Click to accept. The daemon log shows
   `OWO backend owo-primary connected, calibrated and ready`.
4. Open the admin UI at `http://127.0.0.1:7779/`.
5. Pick `compile_error_mild` against `owo-primary` and click FIRE.
6. Brief sensation on the suit confirms end-to-end works.

`grpcurl` flow for the same:

```sh
grpcurl -plaintext localhost:7777 smited.v1.SmitedService/Health
# Expected: backends includes owo-primary with status BACKEND_STATUS_READY.

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

## Production (MyOWO consumer app)

The MyOWO consumer app requires a registered project ID and a signed
`.owoauth` file. Both are obtained by emailing `devs@owogame.com` per
the OWO docs.

1. Register the project at OWO. Email `devs@owogame.com` with developer
   name, project name, sensation list, and an executable. See the OWO
   docs at
   [`https://owo-game.gitbook.io/owo-api/welcome/configure-your-project`](https://owo-game.gitbook.io/owo-api/welcome/configure-your-project).
2. OWO sends back a project ID (a number/string assigned to your
   project) and a `.owoauth` file with baked sensations.
3. Configure smited with both:

   ```json
   {
     "Kind": "owo_skin",
     "Id": "owo-primary",
     "Enabled": true,
     "Options": {
       "ProjectId": "12345",
       "AuthFilePath": "C:\\path\\to\\smited.owoauth",
       "ConnectTimeoutSeconds": 10
     }
   }
   ```

4. The smited daemon configures the SDK with `GameAuth.Parse` of the
   `.owoauth` contents on startup. The MyOWO app's "Scan Games" panel
   should then list the project under its registered name.

`AuthString` (inline `.owoauth` contents) is supported as an
alternative to `AuthFilePath` — useful for tests or for keeping auth
out of the filesystem in containerized deployments. When both are set
`AuthFilePath` wins and the daemon logs a warning.

The unsigned-auth path (no `AuthFilePath` / `AuthString`) is what makes
the Visualizer work for dev. MyOWO silently ignores unsigned auth, so
omitting the auth file is what produced the "game doesn't show in
Scan Games" symptom that motivated this refactor.

## Prerequisites

- **Windows 10 or 11.** The OWO SDK and both OWO desktop apps
  (Visualizer and MyOWO) are Windows-only. Mac/Linux daemons run with
  `MockOwoBackend` only — no shim available for the real device on
  those platforms.
- **An OWO Skin device, paired and fully calibrated** through whichever
  desktop app you use. Calibration is mandatory: the SDK refuses to
  send sensations to an uncalibrated user.
- **The OWO desktop app running in the background** for the entire
  lifetime of the daemon. The SDK talks to the desktop app over local
  TCP; the app holds the Bluetooth pairing with the device. Closing
  the app drops the daemon→device transport.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| Daemon log shows `OWO assembly load failed` | `Smited.Daemon.Owo.dll` is missing from the daemon output | Run `dotnet publish src/Smited.Daemon -c Debug -r win-x64`; verify `_TargetingWindows` evaluates true |
| Daemon log shows `OWO backend owo-primary did not connect within Ns` | Visualizer/MyOWO didn't accept the game in time | Open the app, accept the project ID in Scan Games, check Windows Firewall |
| Game doesn't appear in MyOWO's Scan Games panel | Unsigned auth (no real project ID + `.owoauth`) | Use the Visualizer for dev, OR register the project with OWO and set `AuthFilePath` |
| Game appears in MyOWO but suit doesn't fire | Suit not paired / calibrated, or calibration drift | Re-pair via the desktop app's Devices tab; recalibrate. Gel pads need direct skin contact and lose conductivity after ~50 sessions |
| Daemon banner shows OWO as registered but `Health` reports `DISCONNECTED` | Transport drop detected by the heartbeat poll | Check the desktop app is running. The daemon attempts reconnect with exponential backoff up to `MaxReconnectAttempts`; on exhaustion `Status` flips to `ERROR` |
| Banner doesn't show OWO at all on Windows | OWO descriptor missing or factory declined | Confirm an `owo_skin` descriptor exists in `Smited:Backends:Items` with `Enabled: true`. Check the log for `Could not read AuthFilePath` (BackendConfigurationException) — that means the file path is wrong |
| `accepted=true` but no sensation on skin | Stale calibration or worn-out gel pads | Recalibrate. Replace gel pads if older than ~50 sessions |

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
