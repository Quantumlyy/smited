# PiShock integration

smited's `PishockBackend` (in `src/Smited.Daemon.Pishock/`) drives
PiShock electrical-stimulation devices over either the manufacturer's
cloud API or a direct LAN connection. Unlike the OWO backend, PiShock
is **cross-platform** (no platform-conditional MSBuild) and
**multi-instance** (each shocker is its own descriptor). This document
covers prerequisites, configuration, sensation authoring, and the
manufacturer's safety guidance.

## Prerequisites

- **A PiShock device.** Cloud mode works with any PiShock-branded
  shocker that's been paired through the PiShock account UI. LAN mode
  works with shockers running firmware that exposes the local HTTP
  API (typically the openshock-compatible firmware; the original
  PiShock LAN firmware's exact shape may differ — see § LAN setup).
- **A PiShock account** with credentials (username + API key) and a
  per-shocker share code, all generated through
  https://pishock.com/. Cloud mode requires all three; LAN mode
  doesn't use PiShock account credentials at all.
- **No platform-specific runtime.** Mac, Linux, and Windows daemons
  all support PiShock identically.

## Cloud vs LAN

| Trait                            | Cloud                            | LAN                                  |
| -------------------------------- | -------------------------------- | ------------------------------------ |
| Authentication                   | Username + API key + share code  | None — network reachability is auth  |
| Network                          | Outbound HTTPS to do.pishock.com | Direct HTTP to device on local LAN   |
| Min duration                     | 1 second (whole-second wire)     | Milliseconds (no rounding)           |
| Latency                          | ~150–500ms (round-trip + cloud)  | <50ms typically                      |
| Works off-LAN                    | Yes                              | No                                   |
| Setup complexity                 | Lower (cloud handles networking) | Higher (need IP + routing)           |

**Recommendation:** LAN for sub-second pattern playback (multi-pulse
sensations like `deploy_success`), cloud for "fire a 1-second vibrate
when X happens" patterns where the latency is fine.

A daemon can run cloud-mode and LAN-mode descriptors side by side —
each descriptor picks `Mode: "Cloud"` or `Mode: "Lan"` independently.

## Configuration reference

Every option lives under `Smited:Backends:Items:{i}:Options` for a
descriptor with `Kind: "pishock"` (or `"mock_pishock"` — the mock
takes the same options).

```json
{
  "Kind": "pishock",
  "Id": "pishock-left-thigh",
  "Enabled": true,
  "DisplayName": "Left thigh",
  "Options": {
    "Mode": "Cloud",
    "Username": "alice",
    "ApiKey": "...",
    "ShareCode": "ABCD1234",
    "AllowedOps": ["Vibrate", "Beep"],
    "MaxIntensityShock": 30,
    "MaxIntensityVibrate": 100,
    "MaxDurationMs": 1500,
    "MaxOpsPerSecond": 1,
    "MaxBurst": 3,
    "RequestTimeoutMs": 5000
  }
}
```

| Field                  | Default                  | Meaning |
| ---------------------- | ------------------------ | ------- |
| `Mode`                 | `Cloud`                  | Transport selector. `Cloud` or `Lan`. |
| `Username`             | (required for Cloud)     | PiShock account username. |
| `ApiKey`               | (required for Cloud)     | PiShock API key, generated under Account → Edit. |
| `ShareCode`            | (required for Cloud)     | Per-shocker share code. |
| `DeviceIp`             | (required for Lan)       | IPv4 of the device on the local network. |
| `DevicePort`           | `80`                     | TCP port for LAN HTTP requests. |
| `AllowedOps`           | `["Vibrate", "Beep"]`    | Op allow-list — see § AllowedOps. |
| `MaxIntensityShock`    | `30`                     | Hard cap on Shock intensity (0..100). |
| `MaxIntensityVibrate`  | `100`                    | Hard cap on Vibrate intensity (0..100). |
| `MaxDurationMs`        | `1500`                   | Hard cap on per-op duration. |
| `MaxOpsPerSecond`      | `1`                      | Token-bucket refill rate. |
| `MaxBurst`             | `3`                      | Token-bucket capacity. |
| `RequestTimeoutMs`     | `5000`                   | HTTP request timeout. |
| `DisplayName`          | (descriptor Id)          | Human-readable name; falls back to Id. |

The factory throws `BackendConfigurationException` at startup with the
offending field named for any missing required field, out-of-range
intensity cap, or non-positive duration / rate-limit value.

## AllowedOps

`AllowedOps` is a per-descriptor allow-list of which PiShock op types
the daemon will forward to that device. Three valid entries:

- `"Vibrate"` — buzzing motor, no electrical stimulation.
- `"Beep"` — audible buzzer, no stimulation.
- `"Shock"` — electrical pulse.

The default `["Vibrate", "Beep"]` excludes Shock deliberately. Users
opt into Shock per-shocker — there is no global "enable shock"
switch. The bundled sensation library (`sensations/pishock/`) ships
**no Shock entries**; users author their own once they've enabled
Shock in `AllowedOps`.

Triggers requesting a disallowed op fail two ways:

1. **At sensation-load time:** The sensation file's `op` parameter
   declares an enum value. The backend's `ParameterSchema` lists only
   the allowed enum values for this descriptor's `AllowedOps`, so a
   sensation file with `op=Shock` against a vibrate-only descriptor
   gets rejected at startup by `SensationValidator` with a structured
   `INVALID_PARAMETER`.
2. **At trigger time:** Inline (non-library) triggers via
   `inline_microsensations` bypass the schema-binding sensation
   loader. The backend's runtime validator catches these and rejects
   with the same `INVALID_PARAMETER` code.

## Rate limiter

Each descriptor has a token bucket with capacity `MaxBurst` and refill
rate `MaxOpsPerSecond`. One token is consumed per microsensation in a
trigger (so a 3-pulse sequence costs 3 tokens). If the bucket can't
fund the entire sequence, the trigger is rejected atomically with
`TRIGGER_ERROR_CODE_RATE_LIMITED` — partial sequences would silently
drop pulses the user authored.

**Defaults:** `MaxOpsPerSecond: 1`, `MaxBurst: 3`. This allows three
single-pulse triggers in rapid succession, then enforces 1 op/s
sustained.

**Pattern-heavy sensations:** A 5-pulse sensation needs `MaxBurst >= 5`
to fire as one trigger. Either bump `MaxBurst` for that descriptor or
split the sensation across multiple triggers spaced by the refill rate.

The token bucket is independent of the daemon's `ConcurrencyModel`
(which is `MaxConcurrent: 1, Policy: RejectNew` for PiShock — the
device is single-channel and the wire protocol has no
"cancel-in-progress" message, so a follow-up trigger arriving during
an in-flight op gets `TRIGGER_ERROR_CODE_RATE_LIMITED` rather than
silently overlapping the previous pulse on the device). The bucket
prevents runaway request rates; concurrency prevents overlapping
playback.

## Forbidden regions

The backend declares the following body regions as
manufacturer-mandated forbidden — `IHapticBackend.ForbiddenRegions`,
non-overridable by `AllowOverrideRegions`:

- `Head`, `Face`, `Throat`, `Neck`
- `ChestFront`, `ChestOverHeart`
- `BackUpper`, `BackLower` (the spine)

The chart-faithful PiShock guidance is "neck, spine, chest"; smited
adds `Head` and `Face` because electrical stimulation above the neck
is universally bad regardless of manufacturer guidance.

The bodymap validator refuses to register a PiShock backend whose
`Smited:BodyMap:Placements` declare any zone landing in these regions.
If the user's `AllowOverrideRegions` would unblock a smited-default
region, this list still blocks; only manufacturer-stated bans flow
through `IHapticBackend.ForbiddenRegions`. See
[`docs/body-map.md`](body-map.md) for the full overlap and override
mechanics.

## Sensation authoring

PiShock sensations use the daemon's existing backend-agnostic
parameter format. Each microsensation declares four parameters:

- `op` — Enum of `Vibrate`, `Beep`, or `Shock` (filtered by `AllowedOps`).
- `duration` — Per-op duration as a Duration string (`"0.5s"`, `"100ms"`).
- `intensity` — Number, 0..100. Per-op caps apply at trigger time.
- `delay_before` — Optional Duration. The quiet gap before this microsensation fires.

Single-pulse sensation:

```json
{
  "name": "compile_error_mild",
  "backend_kind": "pishock",
  "display_name": "Compile Error (Mild)",
  "default_zone_ids": ["shock"],
  "default_intensity": 30,
  "estimated_duration": "0.5s",
  "definition": {
    "microsensations": [
      {
        "parameters": {
          "op":        { "enum_value": "vibrate" },
          "duration":  { "duration":   "0.5s" },
          "intensity": { "number":     30 }
        }
      }
    ]
  }
}
```

Multi-pulse sensation:

```json
{
  "name": "deploy_success",
  "backend_kind": "pishock",
  "display_name": "Deploy Success",
  "default_zone_ids": ["shock"],
  "default_intensity": 50,
  "estimated_duration": "0.7s",
  "definition": {
    "microsensations": [
      { "parameters": { "op": {"enum_value": "vibrate"}, "duration": {"duration": "0.1s"}, "intensity": {"number": 50} } },
      { "parameters": { "op": {"enum_value": "vibrate"}, "duration": {"duration": "0.1s"}, "intensity": {"number": 50}, "delay_before": {"duration": "0.2s"} } },
      { "parameters": { "op": {"enum_value": "vibrate"}, "duration": {"duration": "0.1s"}, "intensity": {"number": 50}, "delay_before": {"duration": "0.2s"} } }
    ]
  }
}
```

`estimated_duration` is the sum of every microsensation's
`delay_before + duration`. For the example above:
`100 + (200+100) + (200+100) = 700ms`. The bundled-sensation
authoring tests verify this sum matches.

## Smoke-test runbook

Step-by-step verification from device-just-plugged-in to
firing-bundled-sensations is in [`docs/pishock-smoke.md`](pishock-smoke.md),
including the pre-flight scratch app under `scripts/pishock-smoke/`
and the LAN-firmware-shape verification step.

## Panic button

`POST http://127.0.0.1:7778/panic` cancels every active sensation,
including PiShock pulses in flight. **Important caveat:** PiShock's
wire protocol has no "cancel an in-progress op" message. Panic frees
the daemon's concurrency slots and stops the playback task, but a
pulse already in flight on the device runs to its authored duration.
Keep `MaxDurationMs` short enough that this gap is acceptable for
your use case.

The bundled defaults (`MaxDurationMs: 1500`) give at most 1.5 seconds
of unstoppable in-flight time. Pattern-heavy sensations split into
many short pulses are also more responsive to panic than one long
pulse.

## Safety notes

PiShock is an electrical-stimulation device. The manufacturer's
safety documentation applies — read it before pairing. Highlights:

- **Never use over the heart, throat, neck, head, or spine.** The
  daemon's `ForbiddenRegions` enforces this for declared placements,
  but users can still configure devices physically wherever; the
  daemon can't see the wire.
- **Don't share electrodes between users.** Skin contact and
  stimulation are personal.
- **Stop immediately if anything feels off.** Tingling that turns
  painful, skin redness, anything unusual.
- **Start conservative.** Low intensity, short duration, watch for
  reactions before increasing either. The bundled
  `MaxIntensityShock: 30` and `MaxDurationMs: 1500` defaults are
  starting points, not ceilings.
- **The smited daemon's caps are additional, not primary.** They
  catch authoring mistakes and accidental over-firing; they do not
  replace the user's own attention or the manufacturer's guidance.

The daemon's panic button (`POST /panic`) and per-trigger
`AllowedOps`/intensity-cap rejections are a defense-in-depth layer.
They are not a substitute for the user being aware of what the
device is doing.
