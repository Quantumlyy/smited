# PiShock smoke-test runbook

Step-by-step path from "I just plugged in the device" to "I'm firing
a calibrated, rate-limited sensation from the smited admin UI."
Pre-flight verification with the scratch console app first, then
daemon configuration, then full-loop verification.

## 0. Pre-flight: verify credentials and network with the scratch app

Before plugging anything into the daemon, confirm the device is
reachable and the credentials work using the scratch app under
`scripts/pishock-smoke/`. This catches "I copy-pasted the share code
wrong" before the daemon's backend status flips to ERROR with a less
helpful message.

**Cloud mode:**

```sh
dotnet run --project scripts/pishock-smoke -- \
    --mode cloud \
    --username <your-pishock-username> \
    --apikey <your-pishock-api-key> \
    --sharecode <per-shocker-share-code> \
    --op vibrate --duration 200 --intensity 20
```

The PiShock account UI generates the API key and per-shocker share
codes; both live under "Account" → "Edit" and per-shocker entries
respectively.

Expected output:

```
Sending Vibrate for 200ms at 20% via Cloud...
Accepted:    True
Raw body:    Operation Succeeded.
```

The device should fire a brief light vibration. If the response is
anything other than `Operation Succeeded.`:

| Body                                | Cause                                                  |
| ----------------------------------- | ------------------------------------------------------ |
| `Not Authorized.`                   | Wrong username / API key. Re-check both.               |
| `Shocker is offline.`               | Device powered off or out of Wi-Fi range.              |
| `Operation not permitted.`          | The share code's permissions don't include the op.     |
| `This shocker has been paused.`     | Toggle the share code's "Paused" flag in the account UI. |

**LAN mode:**

```sh
dotnet run --project scripts/pishock-smoke -- \
    --mode lan \
    --ip 192.168.1.50 \
    --op vibrate --duration 200 --intensity 20
```

Find the device's IP from your router's DHCP lease list or the
PiShock mobile app's device-info screen.

Expected output:

```
Sending Vibrate for 200ms at 20% via Lan...
Accepted:    True
Raw body:    (firmware-specific)
```

If LAN mode returns a non-2xx HTTP status with body
`{"error":"unknown route"}` or similar, the firmware on your device
likely uses a different endpoint path than the openshock-compatible
contract this client encodes (`POST /api/1/operate`). Capture one
request from the PiShock mobile app on the same LAN with `tcpdump`
or a transparent proxy (mitmproxy) and update
`src/Smited.Daemon.Pishock/Internal/LanPishockClient.cs` to match.

## 1. Configure smited

Drop the sample at `docs/pishock-config-example.json` into your user
config (`~/.config/smited/config.json` on Linux/Mac,
`%APPDATA%\smited\config.json` on Windows). Replace the
`REPLACE_ME` values with the real credentials. Adjust intensity caps
and `MaxDurationMs` to your tolerance — the example ships with
conservative defaults.

The example wires up two shockers, one per transport, with
descriptors `pishock-left-thigh` (cloud) and `pishock-right-calf`
(LAN). Single-device users delete one of the items.

## 2. Restart smited and verify the banner

```sh
dotnet run --project src/Smited.Daemon
```

The startup banner should report:

```
Backends    2 registered (pishock × 2)
Body map    2 placements
```

If the banner shows zero PiShock backends, check the log for
`Factory for kind pishock declined to create descriptor` — that means
options validation failed. The factory throws
`BackendConfigurationException` naming the missing or out-of-range
field; fix the config and restart.

## 3. Verify backends are READY via gRPC

```sh
grpcurl -plaintext localhost:7777 smited.v1.SmitedService/Health
# Expected: backends includes both pishock-left-thigh and
# pishock-right-calf with status BACKEND_STATUS_READY.

grpcurl -plaintext -d '{"backend_id":"pishock-left-thigh"}' \
    localhost:7777 smited.v1.SmitedService/DescribeBackend
# Expected: 1 zone (shock), 4 parameters (op, duration, intensity,
# delay_before), no calibration state, no zone groups.
```

## 4. Fire each bundled sensation

```sh
grpcurl -plaintext -d '{
    "backend_id":"pishock-left-thigh",
    "sensation_name":"compile_error_mild"
}' localhost:7777 smited.v1.SmitedService/Trigger
# Expected: accepted=true. Brief 500ms vibrate on the left thigh.
```

Iterate the rest:

| Sensation              | Expected feel                                  |
| ---------------------- | ---------------------------------------------- |
| `compile_error_mild`   | Brief 500ms vibrate at 30%                     |
| `compile_error_severe` | Sustained 1s vibrate at 60%                    |
| `deploy_success`       | Three short vibrates with 200ms gaps           |
| `deploy_failure`       | One firm 800ms vibrate at 70%                  |
| `pr_merged`            | Soft vibrate then a confirmation beep          |
| `notification`         | 100ms beep                                     |

## 5. Verify single-channel concurrency

PiShock is single-channel — the device fires one op at a time, and
the daemon refuses overlapping triggers with
`TRIGGER_ERROR_CODE_RATE_LIMITED`. Fire `compile_error_mild` twice
back-to-back without waiting:

```sh
grpcurl -plaintext -d '{
    "backend_id":"pishock-left-thigh",
    "sensation_name":"compile_error_mild"
}' localhost:7777 smited.v1.SmitedService/Trigger &

grpcurl -plaintext -d '{
    "backend_id":"pishock-left-thigh",
    "sensation_name":"compile_error_mild"
}' localhost:7777 smited.v1.SmitedService/Trigger
```

Expected: the first call returns `accepted: true`, the second
returns `accepted: false` with
`error.code: TRIGGER_ERROR_CODE_RATE_LIMITED`. This is the
`MaxConcurrent: 1` + `Policy: RejectNew` enforcement — the device
can't fire two ops at once, so the daemon refuses the overlap
rather than silently sending it.

Wait for the slot to free (RequestTimeoutMs + the sensation's
duration) and the next trigger goes through.

## 5b. Verify the token bucket independently

The token bucket sits underneath the concurrency gate and protects
multi-pulse sensations from exceeding the configured burst budget.
With default `MaxBurst: 3`, a 5-pulse sensation needs 5 tokens at
trigger time, so it's rejected with `RATE_LIMITED` at the
backend's pre-allocation step before any wire traffic happens.

Bump `MaxBurst` for one descriptor to `2` in your config, restart,
and trigger a 3-pulse sensation:

```sh
grpcurl -plaintext -d '{
    "backend_id":"pishock-left-thigh",
    "sensation_name":"deploy_success"
}' localhost:7777 smited.v1.SmitedService/Trigger
# Expected: accepted=false, error.code=RATE_LIMITED, message like
# "trigger needs 3 bucket tokens; bump MaxBurst or slow down the
# trigger rate".
```

Restore `MaxBurst: 3` (or higher for pattern-heavy sensations).

## 6. Verify AllowedOps gating

Author a sensation requesting an op the descriptor doesn't allow.
The example's `pishock-right-calf` has `AllowedOps: ["Vibrate"]`
only, so a beep should be rejected:

```sh
grpcurl -plaintext -d '{
    "backend_id":"pishock-right-calf",
    "inline": {
      "microsensations": [{
        "parameters": {
          "op": { "enum_value": "beep" },
          "duration": { "duration": "0.1s" },
          "intensity": { "number": 30 }
        }
      }]
    }
}' localhost:7777 smited.v1.SmitedService/Trigger
# Expected: accepted=false, error.code=INVALID_PARAMETER,
# error.field=microsensations[0].parameters.op
```

## 7. (Optional) Opt into Shock and verify

The bundled sensation library has no Shock entries. To verify shock
end-to-end, edit your config to add `"Shock"` to one descriptor's
`AllowedOps`:

```json
"AllowedOps": ["Vibrate", "Beep", "Shock"],
"MaxIntensityShock": 10
```

Restart, then author and fire a low-intensity short shock:

```sh
grpcurl -plaintext -d '{
    "backend_id":"pishock-left-thigh",
    "inline": {
      "microsensations": [{
        "parameters": {
          "op": { "enum_value": "shock" },
          "duration": { "duration": "0.1s" },
          "intensity": { "number": 5 }
        }
      }]
    }
}' localhost:7777 smited.v1.SmitedService/Trigger
```

**Stop here for the first session.** Don't crank intensity until
you have a feel for how the device lands at low values. The
manufacturer's safety guidance applies — see
`docs/pishock.md` § Safety.

## Panic button

`POST http://127.0.0.1:7778/panic` cancels every active sensation
across every backend, including PiShock. Bind it to a Stream Deck
button or hardware emergency stop. Note: PiShock's wire protocol has
no "cancel an in-progress op" message; panic frees the daemon's
concurrency slots immediately, but a pulse already in flight on the
device finishes its authored duration before stopping. Keep
`MaxDurationMs` short enough that this delay is acceptable for your
use case.
